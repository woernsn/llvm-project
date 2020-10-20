; NOTE: Assertions have been autogenerated by utils/update_test_checks.py
; RUN: opt < %s -instcombine -S | FileCheck %s

; rdar://6480438
target datalayout = "e-p:32:32:32-i1:8:8-i8:8:8-i16:16:16-i32:32:32-i64:32:64-f32:32:32-f64:32:64-v64:64:64-v128:128:128-a0:0:64-f80:128:128"
target triple = "i386-apple-darwin9.6"
	%struct.Key = type { { i32, i32 } }
	%struct.anon = type <{ i8, [3 x i8], i32 }>

define i32 @bar(i64 %key_token2) nounwind {
; CHECK-LABEL: @bar(
; CHECK-NEXT:  entry:
; CHECK-NEXT:    [[IOSPEC:%.*]] = alloca [[STRUCT_KEY:%.*]], align 8
; CHECK-NEXT:    [[RET:%.*]] = alloca i32, align 4
; CHECK-NEXT:    [[TMP0:%.*]] = getelementptr inbounds [[STRUCT_KEY]], %struct.Key* [[IOSPEC]], i32 0, i32 0, i32 0
; CHECK-NEXT:    store i32 0, i32* [[TMP0]], align 8
; CHECK-NEXT:    [[TMP1:%.*]] = getelementptr inbounds [[STRUCT_KEY]], %struct.Key* [[IOSPEC]], i32 0, i32 0, i32 1
; CHECK-NEXT:    store i32 0, i32* [[TMP1]], align 4
; CHECK-NEXT:    [[TMP2:%.*]] = bitcast %struct.Key* [[IOSPEC]] to i64*
; CHECK-NEXT:    store i64 [[KEY_TOKEN2:%.*]], i64* [[TMP2]], align 8
; CHECK-NEXT:    [[TMP3:%.*]] = call i32 (...) @foo(%struct.Key* nonnull byval align 4 [[IOSPEC]], i32* nonnull [[RET]]) [[ATTR0:#.*]]
; CHECK-NEXT:    [[TMP4:%.*]] = load i32, i32* [[RET]], align 4
; CHECK-NEXT:    ret i32 [[TMP4]]
;
entry:
  %iospec = alloca %struct.Key		; <%struct.Key*> [#uses=3]
  %ret = alloca i32		; <i32*> [#uses=2]
  %"alloca point" = bitcast i32 0 to i32		; <i32> [#uses=0]
  %0 = getelementptr %struct.Key, %struct.Key* %iospec, i32 0, i32 0		; <{ i32, i32 }*> [#uses=2]
  %1 = getelementptr { i32, i32 }, { i32, i32 }* %0, i32 0, i32 0		; <i32*> [#uses=1]
  store i32 0, i32* %1, align 4
  %2 = getelementptr { i32, i32 }, { i32, i32 }* %0, i32 0, i32 1		; <i32*> [#uses=1]
  store i32 0, i32* %2, align 4
  %3 = getelementptr %struct.Key, %struct.Key* %iospec, i32 0, i32 0		; <{ i32, i32 }*> [#uses=1]
  %4 = bitcast { i32, i32 }* %3 to i64*		; <i64*> [#uses=1]
  store i64 %key_token2, i64* %4, align 4
  %5 = call i32 (...) @foo(%struct.Key* byval align 4 %iospec, i32* %ret) nounwind		; <i32> [#uses=0]
  %6 = load i32, i32* %ret, align 4		; <i32> [#uses=1]
  ret i32 %6
}

declare i32 @foo(...)
