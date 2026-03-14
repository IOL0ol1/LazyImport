using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Fody;
using Xunit;

/// <summary>
/// LazyImport.Fody integration tests.
/// Covers three scenarios:
///   - Dynamic mode (default): converts DllImport extern methods to delegate-based dynamic loading.
///   - Static mode: replaces the native library name with a specified value (e.g., "__Internal"), suitable for static linking.
///   - Include filter: converts only methods whose names match Glob patterns.
/// </summary>
public class IntegrationTests
{
    // Execute weaving once per mode, and share the results across all test methods.
    static readonly TestResult DynamicResult;
    static readonly TestResult StaticResult;
    static readonly TestResult FilterResult;

    static IntegrationTests()
    {
        // Dynamic mode — convert all extern methods from "mylib" into delegate invocations.
        DynamicResult = new ModuleWeaver
        {
            Config = XElement.Parse("<LazyImport><Library Name=\"mylib\" /></LazyImport>")
        }.ExecuteTestRun("AssemblyToProcess.dll", runPeVerify: false);

        // Static mode — replace "mylib" with "__Internal" (for static-linking scenarios such as Xamarin.iOS).
        StaticResult = new ModuleWeaver
        {
            Config = XElement.Parse("<LazyImport><Library Name=\"mylib\" ReplaceName=\"__Internal\" /></LazyImport>")
        }.ExecuteTestRun("AssemblyToProcess.dll", runPeVerify: false);

        // Dynamic mode + Include filter — convert only methods whose names match "test_basic_*" and "test_string_*".
        FilterResult = new ModuleWeaver
        {
            Config = XElement.Parse("<LazyImport><Library Name=\"mylib\" Include=\"test_basic_*;test_string_*\" /></LazyImport>")
        }.ExecuteTestRun("AssemblyToProcess.dll", runPeVerify: false);
    }

    // ─── Dynamic mode: Initialize method injection ───────────────────────────

    [Fact]
    public void Dynamic_InitializeStringOverload_IsInjected()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;
        var method = type.GetMethod(
            "Initialize",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string) },
            null);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    [Fact]
    public void Dynamic_InitializeIntPtrOverload_IsInjected()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;
        var method = type.GetMethod(
            "Initialize",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(IntPtr) },
            null);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    // ─── Dynamic mode: extern methods are no longer P/Invoke ─────────────────

    [Fact]
    public void Dynamic_ExternMethods_AreNoLongerPInvokeImpl()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;
        var testMethods = new[]
        {
            "test_basic_simple_return",
            "test_basic_void_with_param",
            "test_basic_param_and_return",
            "test_basic_with_entrypoint",
            "test_basic_string_lpstr",
            "test_string_unicode",
            "test_array_with_size_param_index",
            "test_struct_by_value",
            "test_callback_set_logger"
        };

        foreach (var name in testMethods)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            Assert.False(
                method.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
                $"{name} should no longer have the PInvokeImpl flag after weaving");
        }
    }

    [Fact]
    public void Dynamic_ExternMethod_ThrowsNullRefWithoutInitialize()
    {
        // The .NET 5+ path uses function pointers (calli), and invoking a null pointer can crash the process directly
        // (AccessViolationException) and cannot be reliably caught; this behavior can only be safely validated on the
        // delegate path (.NET 4.x).
        if (Environment.Version.Major >= 5) return;

        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;
        var method = type.GetMethod("test_basic_simple_return", BindingFlags.Public | BindingFlags.Static)!;
        var ex = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, Array.Empty<object>()));
        Assert.IsType<NullReferenceException>(ex.InnerException);
    }

    // ─── Dynamic mode: helper field injection ────────────────────────────────

    [Fact]
    public void Dynamic_HandleField_IsInjected()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;
        var field = type.GetField("__libraryHandle_mylib", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(typeof(IntPtr), field!.FieldType);
    }

    [Fact]
    public void Dynamic_BackingDelegateField_IsInjectedForEachMethod()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;
        var testMethods = new[]
        {
            "test_basic_simple_return",
            "test_basic_void_with_param",
            "test_string_unicode",
            "test_array_with_size_param_index",
            "test_struct_by_value"
        };

        foreach (var name in testMethods)
        {
            var field = type.GetField($"__fn_mylib_{name}", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
        }
    }

    // ─── Static mode ─────────────────────────────────────────────────────────

    [Fact]
    public void Static_ExternMethods_RemainPInvokeImpl()
    {
        var type = StaticResult.Assembly.GetType("MyNativeLib")!;
        var testMethods = new[]
        {
            "test_basic_simple_return",
            "test_basic_void_with_param",
            "test_basic_param_and_return"
        };

        foreach (var name in testMethods)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            Assert.True(
                method.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
                $"In static mode, {name} should retain the PInvokeImpl flag");
        }
    }

    [Fact]
    public void Static_NoInitializeMethod_IsInjected()
    {
        var type = StaticResult.Assembly.GetType("MyNativeLib")!;
        var method = type.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
        Assert.Null(method);
    }

    [Fact]
    public void Static_DllImportLibraryName_IsReplaced()
    {
        var type = StaticResult.Assembly.GetType("MyNativeLib")!;
        var method = type.GetMethod("test_basic_simple_return", BindingFlags.Public | BindingFlags.Static)!;
        var attr = (DllImportAttribute?)Attribute.GetCustomAttribute(method, typeof(DllImportAttribute));
        Assert.NotNull(attr);
        Assert.Equal("__Internal", attr!.Value);
    }

    // ─── Include filter ───────────────────────────────────────────────────────

    [Fact]
    public void Filter_IncludedMethod_IsTransformed()
    {
        var type = FilterResult.Assembly.GetType("MyNativeLib")!;

        // test_basic_* should be transformed
        var basicMethod = type.GetMethod("test_basic_simple_return", BindingFlags.Public | BindingFlags.Static)!;
        Assert.False(
            basicMethod.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
            "test_basic_simple_return matches the Include pattern and should be transformed");

        // test_string_* should be transformed
        var stringMethod = type.GetMethod("test_string_unicode", BindingFlags.Public | BindingFlags.Static)!;
        Assert.False(
            stringMethod.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
            "test_string_unicode matches the Include pattern and should be transformed");
    }

    [Fact]
    public void Filter_ExcludedMethod_IsNotTransformed()
    {
        var type = FilterResult.Assembly.GetType("MyNativeLib")!;

        // test_array_* is not in the filter and should not be transformed
        var arrayMethod = type.GetMethod("test_array_with_size_param_index", BindingFlags.Public | BindingFlags.Static)!;
        Assert.True(
            arrayMethod.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
            "test_array_with_size_param_index does not match the Include pattern and should not be transformed");

        // test_struct_* is not in the filter and should not be transformed
        var structMethod = type.GetMethod("test_struct_by_value", BindingFlags.Public | BindingFlags.Static)!;
        Assert.True(
            structMethod.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
            "test_struct_by_value does not match the Include pattern and should not be transformed");
    }

    [Fact]
    public void Filter_InitializeExists_AndOnlyCoveredMethodHasBackingField()
    {
        var type = FilterResult.Assembly.GetType("MyNativeLib")!;

        // Initialize should exist (at least one method was transformed)
        var init = type.GetMethod(
            "Initialize",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string) },
            null);
        Assert.NotNull(init);

        // Delegate backing field for test_basic_simple_return should exist
        var includedField1 = type.GetField("__fn_mylib_test_basic_simple_return", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(includedField1);

        // Delegate backing field for test_string_unicode should exist
        var includedField2 = type.GetField("__fn_mylib_test_string_unicode", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(includedField2);

        // test_array_with_size_param_index was not transformed, so the delegate backing field should not exist
        var excludedField1 = type.GetField("__fn_mylib_test_array_with_size_param_index", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(excludedField1);

        // test_struct_by_value was not transformed, so the delegate backing field should not exist
        var excludedField2 = type.GetField("__fn_mylib_test_struct_by_value", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(excludedField2);
    }

    // ─── Additional tests: verify different DllImport scenarios ──────────────

    [Fact]
    public void Dynamic_ComplexMarshalAs_IsHandled()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;

        // Test string parameters with MarshalAs
        var method = type.GetMethod("test_basic_string_lpstr", BindingFlags.Public | BindingFlags.Static)!;
        Assert.False(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

        // Test array parameters
        var arrayMethod = type.GetMethod("test_array_with_size_param_index", BindingFlags.Public | BindingFlags.Static)!;
        Assert.False(arrayMethod.Attributes.HasFlag(MethodAttributes.PinvokeImpl));
    }

    [Fact]
    public void Dynamic_CallbackDelegates_AreHandled()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;

        // Test methods with delegate parameters
        var method = type.GetMethod("test_callback_set_logger", BindingFlags.Public | BindingFlags.Static)!;
        Assert.False(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

        // Verify that the delegate type still exists
        var delegateType = type.GetNestedType("LogCallback", BindingFlags.Public);
        Assert.NotNull(delegateType);
    }

    [Fact]
    public void Dynamic_CustomMarshalerMethods_AreHandled()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;

        // Test methods with custom marshalers
        var method = type.GetMethod("test_custom_marshaler_string", BindingFlags.Public | BindingFlags.Static)!;
        Assert.False(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

        // Verify that the custom marshaler class still exists
        var marshalerType = type.GetNestedType("CustomStringMarshaler", BindingFlags.Public);
        Assert.NotNull(marshalerType);
    }

#if NET5_0_OR_GREATER
    [Fact]
    public void Dynamic_FunctionPointerMethods_AreHandled()
    {
        var type = DynamicResult.Assembly.GetType("MyNativeLib")!;

        // Test methods with function pointer parameters (.NET 5+)
        var method = type.GetMethod("test_funcptr_simple_cdecl", BindingFlags.Public | BindingFlags.Static);
        if (method != null)
        {
            Assert.False(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
            "Function pointer methods should be transformed correctly");
        }
    }
#endif
}
