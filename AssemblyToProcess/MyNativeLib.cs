using System;
using System.Runtime.InteropServices;
using System.Text;

#pragma warning disable CA2101

/// <summary>
/// Sample native library wrapper used to test LazyImport.Fody Dynamic and Static modes.
/// This class contains multiple DllImport extern methods for weaver transformation.
/// </summary>
public class MyNativeLib
{
    // ===== Struct definitions =====
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Person
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string name;
        public int age;
    }

    // ===== Delegate definitions =====
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback([MarshalAs(UnmanagedType.LPStr)]string message);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CompareCallback(int a, int b);

    // ===== Custom marshaler class =====
    public class CustomStringMarshaler : ICustomMarshaler
    {
        private static CustomStringMarshaler instance;

        public static ICustomMarshaler GetInstance(string cookie)
        {
            return instance ??= new CustomStringMarshaler();
        }

        public void CleanUpManagedData(object ManagedObj) { }

        public void CleanUpNativeData(IntPtr pNativeData)
        {
            if (pNativeData != IntPtr.Zero)
                Marshal.FreeHGlobal(pNativeData);
        }

        public int GetNativeDataSize() => -1;

        public IntPtr MarshalManagedToNative(object ManagedObj)
        {
            if (ManagedObj == null)
                return IntPtr.Zero;

            var bytes = Encoding.UTF8.GetBytes(ManagedObj.ToString());
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }

        public object MarshalNativeToManaged(IntPtr pNativeData)
        {
            if (pNativeData == IntPtr.Zero)
                return null;

            var length = 0;
            while (Marshal.ReadByte(pNativeData, length) != 0)
                length++;

            var bytes = new byte[length];
            Marshal.Copy(pNativeData, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    // ===== DllImport methods (alphabetical order) =====

    [DllImport("mylib")]
    public static extern void test_array_2d([MarshalAs(UnmanagedType.LPArray, SizeConst = 16)]int[,] matrix);

    [DllImport("mylib")]
    public static extern void test_array_in_out([In, Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]double[] values, int count);

    [DllImport("mylib")]
    public static extern void test_array_with_array_subtype([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U1)]byte[] data, int size);

    [DllImport("mylib")]
    public static extern void test_array_with_size_const([MarshalAs(UnmanagedType.LPArray, SizeConst = 10)]float[] output);

    [DllImport("mylib")]
    public static extern int test_array_with_size_param_index([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]int[] values, int count);

    [DllImport("mylib")]
    public static extern int test_basic_param_and_return(int handle);

    [DllImport("mylib")]
    public static extern int test_basic_simple_return();

    [DllImport("mylib")]
    public static extern void test_basic_string_lpstr([MarshalAs(UnmanagedType.LPStr)]string message);

    [DllImport("mylib")]
    public static extern void test_basic_void_with_param(int handle);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "native_compute_impl")]
    public static extern float test_basic_with_entrypoint(float a, float b);

    [DllImport("mylib")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool test_bool_default([MarshalAs(UnmanagedType.Bool)]bool flag);

    [DllImport("mylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool test_bool_byte([MarshalAs(UnmanagedType.I1)]bool flag);

    [DllImport("mylib")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool test_bool_unsigned_byte([MarshalAs(UnmanagedType.U1)]bool flag);

    [DllImport("mylib")]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    public static extern bool test_bool_variant([MarshalAs(UnmanagedType.VariantBool)]bool flag);

    [DllImport("mylib", CharSet = CharSet.Ansi)]
    public static extern void test_buffer_stringbuilder([MarshalAs(UnmanagedType.LPStr, SizeConst = 256)]StringBuilder output);

    [DllImport("mylib", CharSet = CharSet.Unicode)]
    public static extern int test_buffer_stringbuilder_return([MarshalAs(UnmanagedType.LPWStr)]StringBuilder buffer, int bufferSize);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr test_callback_get_function_pointer(int functionId);

    [DllImport("mylib")]
    public static extern void test_callback_set_logger(LogCallback callback);

    [DllImport("mylib")]
    public static extern void test_callback_sort_array([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]int[] array, int count, CompareCallback compare);

    [DllImport("mylib")]
    public static extern void test_callback_with_userdata(IntPtr callback, IntPtr userData);

    [DllImport("mylib", CallingConvention = CallingConvention.FastCall)]
    public static extern int test_callconv_fastcall(int a, int b, int c);

    [DllImport("mylib", CallingConvention = CallingConvention.StdCall)]
    public static extern int test_callconv_stdcall(int a, int b);

    [DllImport("mylib", CallingConvention = CallingConvention.ThisCall)]
    public static extern void test_callconv_thiscall(IntPtr thisPtr, int value);

    [DllImport("mylib", CallingConvention = CallingConvention.Winapi)]
    public static extern int test_callconv_winapi(int param);

    [DllImport("mylib", 
        EntryPoint = "complex_function",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = false,
        PreserveSig = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    public static extern int test_complex_all_attributes(
        [MarshalAs(UnmanagedType.LPWStr)] string input,
        [In, Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] array,
        int arraySize,
        ref Point point,
        out int status);

    [DllImport("mylib")]
    [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))]
    public static extern string test_custom_marshaler_return();

    [DllImport("mylib")]
    public static extern void test_custom_marshaler_string(
        [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))]
        string message);

    [DllImport("mylib")]
    public static extern void test_custom_marshaler_with_cookie(
        [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler), MarshalCookie = "UTF8")]
        string data);

    [DllImport("mylib", SetLastError = true, ExactSpelling = true)]
    public static extern int test_error_exact_spelling(int value);

    [DllImport("mylib", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool test_error_file_operation([MarshalAs(UnmanagedType.LPWStr)]string filename);

    [DllImport("mylib", SetLastError = true, PreserveSig = false)]
    public static extern void test_error_preserve_sig_false(int param);

    [DllImport("mylib", SetLastError = true)]
    public static extern int test_error_set_last_error(int param);

    [DllImport("mylib", ExactSpelling = true)]
    public static extern void test_exact_spelling_enabled();

    [DllImport("mylib")]
    public static extern void test_marshal_guid([MarshalAs(UnmanagedType.LPStruct)]Guid guid);

    [DllImport("mylib")]
    public static extern void test_marshal_idispatch([MarshalAs(UnmanagedType.IDispatch)]object obj);

    [DllImport("mylib")]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern object test_marshal_interface();

    [DllImport("mylib")]
    public static extern void test_marshal_iunknown([MarshalAs(UnmanagedType.IUnknown)]object obj);

    [DllImport("mylib")]
    public static extern void test_marshal_safearray([MarshalAs(UnmanagedType.SafeArray)]int[] array);

    [DllImport("mylib")]
    public static extern IntPtr test_pointer_alloc_buffer(int size);

    [DllImport("mylib")]
    public static extern unsafe void test_pointer_double_ptr(void** ptrPtr);

    [DllImport("mylib")]
    public static extern void test_pointer_free_buffer(IntPtr buffer);

    [DllImport("mylib")]
    public static extern unsafe int* test_pointer_get_int_ptr(int count);

    [DllImport("mylib")]
    public static extern unsafe void test_pointer_process_byte_ptr(byte* buffer, int size);

    [DllImport("mylib")]
    public static extern UIntPtr test_pointer_uintptr_return(UIntPtr input);

    [DllImport("mylib", PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Error)]
    public static extern int test_preserve_sig_with_error();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true, BestFitMapping = false)]
    public static extern IntPtr test_real_winapi_loadlibrary([MarshalAs(UnmanagedType.LPTStr)] string lpFileName);

    [DllImport("mylib")]
    public static extern int test_refout_multiple_params(int handle, out int status, ref float value);

    [DllImport("mylib")]
    public static extern void test_refout_string_buffer(ref IntPtr buffer, ref int size);

    [DllImport("mylib")]
    public static extern bool test_refout_try_parse([MarshalAs(UnmanagedType.LPStr)]string input, out int result);

    [DllImport("mylib", CharSet = CharSet.Ansi)]
    public static extern void test_string_ansi([MarshalAs(UnmanagedType.LPStr)]string message);

    [DllImport("mylib", CharSet = CharSet.Auto)]
    public static extern void test_string_auto(string message);

    [DllImport("mylib")]
    public static extern void test_string_tstr([MarshalAs(UnmanagedType.LPTStr)]string message);

    [DllImport("mylib", CharSet = CharSet.Unicode)]
    public static extern void test_string_unicode([MarshalAs(UnmanagedType.LPWStr)]string message);

    [DllImport("mylib", CharSet = CharSet.Unicode, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern void test_string_unicode_strict([MarshalAs(UnmanagedType.LPWStr)]string message);

    [DllImport("mylib")]
    public static extern void test_struct_by_value(Point pt);

    [DllImport("mylib")]
    public static extern void test_struct_in_out_param([In, Out] Person person);

    [DllImport("mylib")]
    public static extern void test_struct_out_param(out Point pt);

    [DllImport("mylib")]
    public static extern void test_struct_ref_param(ref Point pt);

    [DllImport("mylib")]
    public static extern IntPtr test_struct_return_pointer();

    [DllImport("mylib")]
    public static extern Point test_struct_return_value();

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern int test_varargs_call_function_pointer(IntPtr varargFuncPtr, [MarshalAs(UnmanagedType.LPStr)]string format, __arglist);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern void test_varargs_log_multiple([MarshalAs(UnmanagedType.LPStr)]string format, __arglist);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern int test_varargs_printf([MarshalAs(UnmanagedType.LPStr)]string format, __arglist);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern void test_varargs_set_function_pointer(IntPtr varargFuncPtr);

#if NET5_0_OR_GREATER
    // ===== Function pointer methods (.NET 5.0+, alphabetical order) =====

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_array(
        delegate* unmanaged[Cdecl]<int, void>* callbacks, 
        int callbackCount);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_complex_return(
        delegate* unmanaged[Cdecl]<int, byte*, int> getBuffer,
        int bufferId);

    [DllImport("mylib", CallingConvention = CallingConvention.FastCall)]
    public static extern unsafe int test_funcptr_fastcall(delegate* unmanaged[Fastcall]<float, float, float> callback, float x, float y);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe delegate* unmanaged[Cdecl]<int, int> test_funcptr_get_callback(int functionId);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_multiple(
        delegate* unmanaged[Cdecl]<int, void> onStart,
        delegate* unmanaged[Cdecl]<int, int, void> onProgress,
        delegate* unmanaged[Cdecl]<int, void> onComplete);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_no_params(delegate* unmanaged[Cdecl]<void> callback);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int test_funcptr_optional(
        delegate* unmanaged[Cdecl]<int, int, int> callback,
        int defaultValue);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe float test_funcptr_return_float(delegate* unmanaged[Cdecl]<float, float, float> computeFunc, float input);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe byte* test_funcptr_return_pointer(
        delegate* unmanaged[Cdecl]<int, byte*> allocator,
        int size);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int test_funcptr_simple_cdecl(delegate* unmanaged[Cdecl]<int, int, int> callback, int a, int b);

    [DllImport("mylib", CallingConvention = CallingConvention.StdCall)]
    public static extern unsafe void test_funcptr_stdcall(delegate* unmanaged[Stdcall]<int, void> callback, int value);

    [DllImport("mylib", CallingConvention = CallingConvention.ThisCall)]
    public static extern unsafe void test_funcptr_thiscall(
        void* thisPtr,
        delegate* unmanaged[Thiscall]<void*, int, void> memberFunc,
        int value);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_with_string(delegate* unmanaged[Cdecl]<byte*, void> callback, 
        [MarshalAs(UnmanagedType.LPStr)] string message);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_with_struct(delegate* unmanaged[Cdecl]<Point*, int> callback, ref Point pt);

    [DllImport("mylib", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void test_funcptr_with_userdata(
        delegate* unmanaged[Cdecl]<void*, int, void> callback, 
        void* userData, 
        int count);
#endif
}
