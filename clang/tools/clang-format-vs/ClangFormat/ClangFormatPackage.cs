﻿//===-- ClangFormatPackages.cs - VSPackage for clang-format ------*- C# -*-===//
//
// Part of the LLVM Project, under the Apache License v2.0 with LLVM Exceptions.
// See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
//
//===----------------------------------------------------------------------===//
//
// This class contains a VS extension package that runs clang-format over a
// selection in a VS text editor.
//
//===----------------------------------------------------------------------===//

using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using System.Diagnostics;
using System.Runtime.Remoting;
using EnvDTE80;
using Microsoft.VisualStudio.VCProjectEngine;
using System.Xml;
using System.Xml.XPath;
using System.Threading;
using System.Threading.Tasks;

namespace LLVM.ClangFormat
{

    [ClassInterface(ClassInterfaceType.AutoDual)]
    [CLSCompliant(false), ComVisible(true)]
    public class OptionPageGrid : DialogPage
    {
        private string assumeFilename = "";
        private string fallbackStyle = "LLVM";
        private bool sortIncludes = false;
        private string style = "file";
        private bool formatOnSave = false;
        private string formatOnSaveFileExtensions =
            ".c;.cpp;.cxx;.cc;.tli;.tlh;.h;.hh;.hpp;.hxx;.hh;.inl;" +
            ".java;.js;.ts;.m;.mm;.proto;.protodevel;.td";
        private string globalClangFile = "%APPDATA%\\vizpkg\\data\\stylesheets\\format\\default.json";

        public OptionPageGrid Clone()
        {
            // Use MemberwiseClone to copy value types.
            var clone = (OptionPageGrid)MemberwiseClone();
            return clone;
        }

        public class StyleConverter : TypeConverter
        {
            protected ArrayList values;
            public StyleConverter()
            {
                // Initializes the standard values list with defaults.
                values = new ArrayList(new string[] { "file", "Chromium", "Google", "LLVM", "Mozilla", "WebKit" });
            }

            public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
            {
                return true;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            {
                return new StandardValuesCollection(values);
            }

            public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
            {
                if (sourceType == typeof(string))
                    return true;

                return base.CanConvertFrom(context, sourceType);
            }

            public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
            {
                string s = value as string;
                if (s == null)
                    return base.ConvertFrom(context, culture, value);

                return value;
            }
        }

        [Category("Format Options")]
        [DisplayName("Style")]
        [Description("Coding style, currently supports:\n" +
                     "  - Predefined styles ('LLVM', 'Google', 'Chromium', 'Mozilla', 'WebKit').\n" +
                     "  - 'file' to search for a YAML .clang-format or _clang-format\n" +
                     "    configuration file.\n" +
                     "  - A YAML configuration snippet.\n\n" +
                     "'File':\n" +
                     "  Searches for a .clang-format or _clang-format configuration file\n" +
                     "  in the source file's directory and its parents.\n\n" +
                     "YAML configuration snippet:\n" +
                     "  The content of a .clang-format configuration file, as string.\n" +
                     "  Example: '{BasedOnStyle: \"LLVM\", IndentWidth: 8}'\n\n" +
                     "This is only used if the 'Global CLang File' is not set!\n\n" +
                     "See also: http://clang.llvm.org/docs/ClangFormatStyleOptions.html.")]
        [TypeConverter(typeof(StyleConverter))]
        public string Style
        {
            get { return style; }
            set { style = value; }
        }

        public sealed class FilenameConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
            {
                if (sourceType == typeof(string))
                    return true;

                return base.CanConvertFrom(context, sourceType);
            }

            public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
            {
                string s = value as string;
                if (s == null)
                    return base.ConvertFrom(context, culture, value);

                // Check if string contains quotes. On Windows, file names cannot contain quotes.
                // We do not accept them however to avoid hard-to-debug problems.
                // A quote in user input would end the parameter quote and so break the command invocation.
                if (s.IndexOf('\"') != -1)
                    throw new NotSupportedException("Filename cannot contain quotes");

                return value;
            }
        }

        [Category("Format Options")]
        [DisplayName("Assume Filename")]
        [Description("When reading from stdin, clang-format assumes this " +
                     "filename to look for a style config file (with 'file' style) " +
                     "and to determine the language.")]
        [TypeConverter(typeof(FilenameConverter))]
        public string AssumeFilename
        {
            get { return assumeFilename; }
            set { assumeFilename = value; }
        }

        public sealed class FallbackStyleConverter : StyleConverter
        {
            public FallbackStyleConverter()
            {
                // Add "none" to the list of styles.
                values.Insert(0, "none");
            }
        }

        [Category("Format Options")]
        [DisplayName("Fallback Style")]
        [Description("The name of the predefined style used as a fallback in case clang-format " +
                     "is invoked with 'file' style, but can not find the configuration file.\n" +
                     "Use 'none' fallback style to skip formatting.")]
        [TypeConverter(typeof(FallbackStyleConverter))]
        public string FallbackStyle
        {
            get { return fallbackStyle; }
            set { fallbackStyle = value; }
        }

        [Category("Format Options")]
        [DisplayName("Sort includes")]
        [Description("Sort touched include lines.\n\n" +
                     "See also: http://clang.llvm.org/docs/ClangFormat.html.")]
        public bool SortIncludes
        {
            get { return sortIncludes; }
            set { sortIncludes = value; }
        }

        [Category("Format On Save")]
        [DisplayName("Enable")]
        [Description("Enable running clang-format when modified files are saved.\n" +
                     "Will only format if Style is found (ignores Fallback Style).\n" +
                     "Will only format if <AUTOFORMAT>1</AUTOFORMAT> is set on the project."
            )]
        public bool FormatOnSave
        {
            get { return formatOnSave; }
            set { formatOnSave = value; }
        }

        [Category("Format On Save")]
        [DisplayName("File extensions")]
        [Description("When formatting on save, clang-format will be applied only to " +
                     "files with these extensions.")]
        public string FormatOnSaveFileExtensions
        {
            get { return formatOnSaveFileExtensions; }
            set { formatOnSaveFileExtensions = value; }
        }

        [Category("Format Options")]
        [DisplayName("Global CLang File")]
        [Description("The path to the global CLang file that should be used by Visual Studio.")]
        public string GlobalClangFile
        {
            get { return globalClangFile; }
            set { globalClangFile = value; }
        }
    }


    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    ////[ProvideAutoLoad(UIContextGuids80.SolutionExists)] // Load package on solution load
    //[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string)]
    //[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string)]
    //[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasMultipleProjects_string)]
    //[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasSingleProject_string)]
    //[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(GuidList.guidClangFormatPkgString)]
    [ProvideOptionPage(typeof(OptionPageGrid), "LLVM/Clang", "ClangFormat", 0, 0, true)]
    public sealed class ClangFormatPackage : AsyncPackage
    {
        #region Package Members

        RunningDocTableEventsDispatcher _runningDocTableEventsDispatcher;
        static Guid OUTPUT_WINDOW_GUID = new Guid("EEC912A4-A8FB-403D-A03D-8E884DA049A5");
        static string OUTPUT_WINDOW_TITLE = "Clang Format by Viz";
        IVsOutputWindowPane customPane;

        protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            _runningDocTableEventsDispatcher = new RunningDocTableEventsDispatcher(this);
            _runningDocTableEventsDispatcher.BeforeSave += OnBeforeSave;

            IVsOutputWindow outWindow = GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;

            outWindow.CreatePane(ref OUTPUT_WINDOW_GUID, OUTPUT_WINDOW_TITLE, 1, 1);
            outWindow.GetPane(ref OUTPUT_WINDOW_GUID, out customPane);

            var commandService = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                {
                    var menuCommandID = new CommandID(GuidList.guidClangFormatCmdSet, (int)PkgCmdIDList.cmdidClangFormatSelection);
                    var menuItem = new MenuCommand(MenuItemCallback, menuCommandID);
                    commandService.AddCommand(menuItem);
                }

                {
                    var menuCommandID = new CommandID(GuidList.guidClangFormatCmdSet, (int)PkgCmdIDList.cmdidClangFormatDocument);
                    var menuItem = new MenuCommand(MenuItemCallback, menuCommandID);
                    commandService.AddCommand(menuItem);
                }
            }

        }

        #endregion

        OptionPageGrid GetUserOptions()
        {
            return (OptionPageGrid)GetDialogPage(typeof(OptionPageGrid));
        }

        private void MenuItemCallback(object sender, EventArgs args)
        {
            var mc = sender as System.ComponentModel.Design.MenuCommand;
            if (mc == null)
                return;

            switch (mc.CommandID.ID)
            {
                case (int)PkgCmdIDList.cmdidClangFormatSelection:
                    FormatSelection(GetUserOptions());
                    break;

                case (int)PkgCmdIDList.cmdidClangFormatDocument:
                    FormatDocument(GetUserOptions());
                    break;
            }
        }

        private static bool FileHasExtension(string filePath, string fileExtensions)
        {
            var extensions = fileExtensions.ToLower().Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            return extensions.Contains(Path.GetExtension(filePath).ToLower());
        }

        private void OnBeforeSave(object sender, Document document)
        {
            var options = GetUserOptions();

            if (!options.FormatOnSave)
                return;

            if (!FileHasExtension(document.FullName, options.FormatOnSaveFileExtensions))
                return;

            if (!Vsix.IsDocumentDirty(document))
                return;

            DTE dte = (DTE)GetService(typeof(DTE));
            string solutionDir = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
            string autoFormatFile = System.IO.Path.GetFullPath(Path.Combine(solutionDir, @"..\..\.autoformat"));
            if (File.Exists(autoFormatFile))
            {
                customPane.OutputString("-> .autoformat found but not supported any longer. Please remove it and use a CMake property instead on the project:\n");
                customPane.OutputString("\t set_target_properties(<TARGET> PROPERTIES VS_GLOBAL_AUTOFORMAT 1)\n");
            }

            if (!FindAutoFormatProperty())
                return;

            var optionsWithNoFallbackStyle = GetUserOptions().Clone();
            optionsWithNoFallbackStyle.FallbackStyle = "none";
            FormatDocument(document, optionsWithNoFallbackStyle);
        }

        /// <summary>
        /// Runs clang-format on the current selection
        /// </summary>
        private void FormatSelection(OptionPageGrid options)
        {
            IWpfTextView view = Vsix.GetCurrentView();
            if (view == null)
                // We're not in a text view.
                return;
            string text = view.TextBuffer.CurrentSnapshot.GetText();
            int start = view.Selection.Start.Position.GetContainingLine().Start.Position;
            int end = view.Selection.End.Position.GetContainingLine().End.Position;

            // clang-format doesn't support formatting a range that starts at the end
            // of the file.
            if (start >= text.Length && text.Length > 0)
                start = text.Length - 1;
            string path = Vsix.GetDocumentParent(view);
            string filePath = Vsix.GetDocumentPath(view);

            RunClangFormatAndApplyReplacements(text, start, end, path, filePath, options, view);
        }

        /// <summary>
        /// Runs clang-format on the current document
        /// </summary>
        private void FormatDocument(OptionPageGrid options)
        {
            FormatView(Vsix.GetCurrentView(), options);
        }

        private void FormatDocument(Document document, OptionPageGrid options)
        {
            FormatView(Vsix.GetDocumentView(document), options);
        }

        private void FormatView(IWpfTextView view, OptionPageGrid options)
        {
            if (view == null)
                // We're not in a text view.
                return;

            string filePath = Vsix.GetDocumentPath(view);
            var path = Path.GetDirectoryName(filePath);

            string text = view.TextBuffer.CurrentSnapshot.GetText();
            if (!text.EndsWith(Environment.NewLine))
            {
                view.TextBuffer.Insert(view.TextBuffer.CurrentSnapshot.Length, Environment.NewLine);
                text += Environment.NewLine;
            }

            RunClangFormatAndApplyReplacements(text, 0, text.Length, path, filePath, options, view);
        }

        private void RunClangFormatAndApplyReplacements(string text, int start, int end, string path, string filePath, OptionPageGrid options, IWpfTextView view)
        {
            try
            {
                string replacements = RunClangFormat(text, start, end, path, filePath, options);
                if (replacements != "")
                    ApplyClangFormatReplacements(replacements, view);
            }
            catch (Exception e)
            {
                var uiShell = (IVsUIShell)GetService(typeof(SVsUIShell));
                var id = Guid.Empty;
                int result;
                uiShell.ShowMessageBox(
                        0, ref id,
                        "Error while running clang-format:",
                        e.Message,
                        string.Empty, 0,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                        OLEMSGICON.OLEMSGICON_INFO,
                        0, out result);
            }
        }

        /// <summary>
        /// Runs the given text through clang-format and returns the replacements as XML.
        /// 
        /// Formats the text in range start and end.
        /// </summary>
        private string RunClangFormat(string text, int start, int end, string path, string filePath, OptionPageGrid options)
        {
            string vsixPath = Path.GetDirectoryName(
                typeof(ClangFormatPackage).Assembly.Location);

            System.Diagnostics.Process process = new System.Diagnostics.Process();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.FileName = vsixPath + "\\clang-format.exe";
            char[] chars = text.ToCharArray();
            int offset = Encoding.UTF8.GetByteCount(chars, 0, start);
            int length = Encoding.UTF8.GetByteCount(chars, 0, end) - offset;
            // Poor man's escaping - this will not work when quotes are already escaped
            // in the input (but we don't need more).
            string style = options.Style.Replace("\"", "\\\"");
            string fallbackStyle = options.FallbackStyle.Replace("\"", "\\\"");
            process.StartInfo.Arguments = " -offset " + offset +
                                          " -length " + length +
                                          " -output-replacements-xml " +
                                          " -fallback-style \"" + fallbackStyle + "\"";

            customPane.Activate();
            customPane.OutputString("\n============== " + string.Format("{0:HH:mm:ss tt}", DateTime.Now) + " ==============\n");

            bool usingGlobalClangFile = true;

            string globalClangFile = options.GlobalClangFile;
            if (!string.IsNullOrEmpty(globalClangFile))
            {
                if (globalClangFile.ToLower().Contains("%appdata%"))
                {
                    string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    globalClangFile = Regex.Replace(globalClangFile, "%APPDATA%", appDataDir, RegexOptions.IgnoreCase);
                }

                if (!File.Exists(globalClangFile))
                {
                    DialogResult dialogResult = MessageBox.Show("CLang file not found:\n\n" + globalClangFile + "\n\nDo you want to use the fallback method ('" + style + "')?", "CLang file not found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (dialogResult == DialogResult.No)
                    {
                        customPane.OutputString("-> Not formatting file.. \n");
                        return "";
                    }
                }
                else
                {
                    customPane.OutputString("-> USing following file to format: \n");
                    customPane.OutputString("\t" + globalClangFile + "\n");
                    string clangFileContent = File.ReadAllText(globalClangFile, Encoding.UTF8);
                    clangFileContent = Regex.Replace(clangFileContent, @"\r\n?|\n", " ");
                    process.StartInfo.Arguments += " -style \"" + clangFileContent + "\"";
                }
            }
            else
            {
                customPane.OutputString("-> Global clang file is not set!\n");
                usingGlobalClangFile = false;
            }

            if (!usingGlobalClangFile)
                process.StartInfo.Arguments += " -style \"" + style + "\"";

            if (options.SortIncludes)
              process.StartInfo.Arguments += " -sort-includes ";
            string assumeFilename = options.AssumeFilename;
            if (string.IsNullOrEmpty(assumeFilename))
                assumeFilename = filePath;
            if (!string.IsNullOrEmpty(assumeFilename))
              process.StartInfo.Arguments += " -assume-filename \"" + assumeFilename + "\"";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            if (path != null)
                process.StartInfo.WorkingDirectory = path;
            // We have to be careful when communicating via standard input / output,
            // as writes to the buffers will block until they are read from the other side.
            // Thus, we:
            // 1. Start the process - clang-format.exe will start to read the input from the
            //    standard input.
            try
            {
                customPane.OutputString("-> Using following arguments to format:\n");
                customPane.OutputString("\t" + process.StartInfo.Arguments + "\n");
                process.Start();
            }
            catch (Exception e)
            {
                throw new Exception(
                    "Cannot execute " + process.StartInfo.FileName + ".\n\"" + 
                    e.Message + "\".\nPlease make sure it is on the PATH.");
            }
            // 2. We write everything to the standard output - this cannot block, as clang-format
            //    reads the full standard input before analyzing it without writing anything to the
            //    standard output.
            StreamWriter utf8Writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            utf8Writer.Write(text);
            // 3. We notify clang-format that the input is done - after this point clang-format
            //    will start analyzing the input and eventually write the output.
            utf8Writer.Close();
            // 4. We must read clang-format's output before waiting for it to exit; clang-format
            //    will close the channel by exiting.
            string output = process.StandardOutput.ReadToEnd();
            // 5. clang-format is done, wait until it is fully shut down.
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                // FIXME: If clang-format writes enough to the standard error stream to block,
                // we will never reach this point; instead, read the standard error asynchronously.
                throw new Exception(process.StandardError.ReadToEnd());
            }
            customPane.OutputString("-> Formatting finished.\n");
            return output;
        }

        /// <summary>
        /// Applies the clang-format replacements (xml) to the current view
        /// </summary>
        private static void ApplyClangFormatReplacements(string replacements, IWpfTextView view)
        {
            // clang-format returns no replacements if input text is empty
            if (replacements.Length == 0)
                return;

            string text = view.TextBuffer.CurrentSnapshot.GetText();
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            var root = XElement.Parse(replacements);
            var edit = view.TextBuffer.CreateEdit();
            foreach (XElement replacement in root.Descendants("replacement"))
            {
                int offset = int.Parse(replacement.Attribute("offset").Value);
                int length = int.Parse(replacement.Attribute("length").Value);
                var span = new Span(
                    Encoding.UTF8.GetCharCount(bytes, 0, offset),
                    Encoding.UTF8.GetCharCount(bytes, offset, length));
                edit.Replace(span, replacement.Value);
            }
            edit.Apply();
        }

        private bool FindAutoFormatProperty()
        {
            DTE2 dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            Project p = dte.ActiveDocument.ProjectItem.ContainingProject;
            VCProject p2 = (VCProject)p.Object;

            string projectFile = p2.ProjectFile;

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(projectFile);

            XPathNavigator nav = xmlDoc.CreateNavigator();

            nav.MoveToRoot();
            nav.MoveToFirstChild();
            nav.MoveToNext();
            string xmlNs = nav.NamespaceURI;
            XmlNamespaceManager nsMgr = new XmlNamespaceManager(new NameTable());
            nsMgr.AddNamespace("x", xmlNs);
            XPathNodeIterator autoformatIt = nav.Select("//x:AUTOFORMAT", nsMgr);
            if (autoformatIt.Count > 0)
            {
                while (autoformatIt.MoveNext())
                {
                    if (autoformatIt.Current.Value == "1")
                        return true;
                }
            }

            return false;
        }
    }
}
