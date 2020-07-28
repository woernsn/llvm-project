using System;

namespace LLVM.ClangFormat
{
    static class GuidList
    {
        public const string guidClangFormatPkgString = "8fe49268-ed2b-432a-8361-0f9365a66aab";
        public const string guidClangFormatCmdSetString = "999a3308-93f9-43a6-9dac-1794663dd21b";

        public static readonly Guid guidClangFormatCmdSet = new Guid(guidClangFormatCmdSetString);
    };
}
