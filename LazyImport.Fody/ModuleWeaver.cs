using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// LazyImport Fody IL weaver.
///
/// Selects a processing mode for each library independently based on &lt;Library&gt; sub-element
/// configuration in FodyWeavers.xml:
/// <list type="bullet">
///   <item>
///     <b>Dynamic</b> (set the <c>InitMethod</c> attribute, or default when both are omitted) —
///     Converts matching <c>[DllImport] extern</c> methods to dynamic loading using private nested 
///     delegate types + <c>Marshal.GetDelegateForFunctionPointer</c>.
///     <para>
///     <b>Exception:</b> If the original method signature contains <c>delegate* unmanaged</c> 
///     parameters or return type (.NET 5+ only), uses function pointer fields and <c>calli</c> 
///     instructions instead to preserve the unmanaged function pointer semantics.
///     </para>
///     Both paths inject <c>Initialize(IntPtr)</c> and <c>Initialize(string)</c> overloads.
///   </item>
///   <item>
///     <b>Static</b> (set the <c>ReplaceName</c> attribute) —
///     Replaces the <c>[DllImport]</c> library name directly in IL, suitable for static linking.
///   </item>
/// </list>
///
/// <para><b>Variadic parameter handling</b></para>
/// Methods with the <c>VarArg</c> calling convention have their variadic part stripped and are treated as Cdecl;
/// the last <c>params T[]</c> parameter is replaced with <c>IntPtr</c> (caller passes a raw pointer).
///
/// <para><b>Marshal attributes</b></para>
/// The delegate Invoke method's parameters and return value copy the original extern method's
/// <c>MarshalInfo</c> and In/Out <c>ParameterAttributes</c> to preserve marshaling behavior.
/// This ensures correct handling of custom marshalers, strings, arrays, and other complex types.
///
/// <para><b>Include / Exclude filtering</b></para>
/// Each &lt;Library&gt; element supports <c>Include</c>/<c>Exclude</c> attributes with
/// semicolon-separated Glob patterns; <c>*</c> matches any character sequence, <c>?</c> matches a single character.
///
/// <para><b>Configuration example</b></para>
/// <code><![CDATA[
/// <LazyImport>
///   <!-- Dynamic mode: custom initializer method name -->
///   <Library Name="mylib"  InitMethod="Initialize"  Include="lib_abc*" Exclude="lib_abc_2*" />
///   <!-- Static mode: replace DllImport library name -->
///   <Library Name="mylib3" ReplaceName="__Internal" Exclude="my_extra_*" />
/// </LazyImport>
/// ]]></code>
///
/// <para><b>Member naming (Dynamic mode)</b></para>
/// <c>__libraryHandle_{lib}</c>, <c>__fn_{lib}_{method}</c>,
/// <c>__delegate_{lib}_{method}</c>,
/// where <c>{lib}</c> is the library name with non-alphanumeric characters replaced by <c>_</c>.
/// </summary>
public class ModuleWeaver : BaseModuleWeaver
{
    // ─────────────────────────────────────────────────────────────────────────
    // Mode enum
    // ─────────────────────────────────────────────────────────────────────────

    private enum LibraryMode { Dynamic, Static }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-library specification (parsed from <Library> elements)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class LibrarySpec
    {
        /// <summary>Native library name to match (case-insensitive; extension stripped before comparison).</summary>
        public string Name { get; set; } = "";

        /// <summary>Processing mode: Dynamic or Static.</summary>
        public LibraryMode Mode { get; set; } = LibraryMode.Dynamic;

        /// <summary>[Dynamic] Name of the injected Initialize method, from the InitMethod attribute.</summary>
        public string InitMethodName { get; set; } = "Initialize";

        /// <summary>[Static] Replacement library name written into [DllImport], from the ReplaceName attribute.</summary>
        public string ReplaceWith { get; set; } = "";

        /// <summary>Short identifier used to make injected member names unique (normalized form of Name).</summary>
        public string Prefix { get; set; } = "";

        /// <summary>Glob regexes compiled from the Include attribute; empty means include all.</summary>
        public Regex[] IncludePatterns { get; set; } = Array.Empty<Regex>();

        /// <summary>Glob regexes compiled from the Exclude attribute; empty means exclude nothing.</summary>
        public Regex[] ExcludePatterns { get; set; } = Array.Empty<Regex>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extern method binding descriptor (one per method in Dynamic mode)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class ExternBinding
    {
        /// <summary>Original extern method definition.</summary>
        public MethodDefinition Extern { get; set; } = null!;

        /// <summary>Static field storing the delegate instance or function pointer.</summary>
        public FieldDefinition BackingField { get; set; } = null!;

        /// <summary>Delegate type (always present for delegate path).</summary>
        public TypeDefinition? DelegateType { get; set; }

        /// <summary>Function pointer type (only set when original method uses delegate* unmanaged).</summary>
        public FunctionPointerType? FuncPtrType { get; set; }

        /// <summary>Effective parameter list (variadic substitution already applied).</summary>
        public IReadOnlyList<ParameterDefinition> EffectiveParameters { get; set; } = Array.Empty<ParameterDefinition>();

        /// <summary>Whether to use function pointer path (only when original signature contains delegate* unmanaged).</summary>
        public bool UsesFunctionPointer => FuncPtrType != null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parsed specification list
    // ─────────────────────────────────────────────────────────────────────────

    private List<LibrarySpec> _specs = new List<LibrarySpec>();

    // ─────────────────────────────────────────────────────────────────────────
    // Cached common type/method references (Dynamic mode only)
    // ─────────────────────────────────────────────────────────────────────────

    private TypeReference _voidRef = null!;
    private TypeReference _intPtrRef = null!;
    private TypeReference _stringRef = null!;
    private TypeReference _objectRef = null!;
    private TypeReference _multicastDelegateRef = null!;
    private TypeReference _asyncResultRef = null!;
    private TypeReference _asyncCallbackRef = null!;

    /// <summary>Open generic Marshal.GetDelegateForFunctionPointer&lt;T&gt;(IntPtr).</summary>
    private MethodReference _marshalGetDfpOpen = null!;

    /// <summary>Loader Load(string):IntPtr.</summary>
    private MethodReference _nativeLoad = null!;

    /// <summary>Loader GetExport(IntPtr,string):IntPtr.</summary>
    private MethodReference _nativeGetExport = null!;

    public override bool ShouldCleanReference => true;
    
    // ─────────────────────────────────────────────────────────────────────────
    // IModuleWeaver.Execute
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Weaving entry point: parses configuration and dispatches by mode.</summary>
    public override void Execute()
    {
        _specs = ParseLibrarySpecs();

        var dynamicSpecs = _specs.Where(s => s.Mode == LibraryMode.Dynamic).ToList();
        var staticSpecs  = _specs.Where(s => s.Mode == LibraryMode.Static).ToList();

        // Import common types and loader only when Dynamic specs are present
        if (dynamicSpecs.Count > 0)
        {
            ImportCommonTypes();
            ResolveOrInjectNativeLoader();
        }

        if (staticSpecs.Count > 0)
            ExecuteStatic(staticSpecs);

        if (dynamicSpecs.Count > 0)
            ExecuteDynamic(dynamicSpecs);
    }

    /// <summary>Returns the framework assemblies Fody should scan.</summary>
    public override IEnumerable<string> GetAssembliesForScanning()
    {
        yield return "mscorlib";
        yield return "System.Runtime";
        yield return "System.Runtime.InteropServices";
        yield return "netstandard";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Library spec parsing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="LibrarySpec"/> list from the weaver's XML configuration.
    /// <list type="bullet">
    ///   <item>Setting the <c>InitMethod</c> attribute → Dynamic mode.</item>
    ///   <item>Setting the <c>ReplaceName</c> attribute → Static mode.</item>
    ///   <item>Both omitted → default Dynamic mode with initializer name "Initialize".</item>
    ///   <item>No &lt;Library&gt; elements → auto-detect (only valid when a single DLL is found).</item>
    /// </list>
    /// </summary>
    private List<LibrarySpec> ParseLibrarySpecs()
    {
        var libraryElements = Config?.Elements("Library").ToList()
            ?? new List<XElement>();

        // Auto-detect when no Library elements are present
        if (libraryElements.Count == 0)
            return AutoDetectSpecs();

        var specs = new List<LibrarySpec>(libraryElements.Count);
        foreach (var el in libraryElements)
        {
            var name = el.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
                throw new WeavingException(
                    "[LazyImport.Fody] <Library> element is missing required attribute 'Name'.");

            var initMethod  = el.Attribute("InitMethod")?.Value;
            var replaceName = el.Attribute("ReplaceName")?.Value;

            // A library cannot have both modes set at the same time
            if (!string.IsNullOrWhiteSpace(initMethod) && !string.IsNullOrWhiteSpace(replaceName))
                throw new WeavingException(
                    $"[LazyImport.Fody] Library \"{name}\" cannot have both InitMethod (Dynamic) and ReplaceName (Static) set.");

            LibraryMode mode;
            string initMethodName;
            string replaceWith;

            if (!string.IsNullOrWhiteSpace(replaceName))
            {
                // ReplaceName explicitly set → Static mode
                mode           = LibraryMode.Static;
                initMethodName = "";
                replaceWith    = replaceName!;
            }
            else
            {
                // InitMethod set or both omitted → Dynamic mode
                mode           = LibraryMode.Dynamic;
                initMethodName = !string.IsNullOrWhiteSpace(initMethod) ? initMethod! : "Initialize";
                replaceWith    = "";
            }

            var prefix = NormalizeName(name!);
            specs.Add(new LibrarySpec
            {
                Name            = name!,
                Mode            = mode,
                InitMethodName  = initMethodName,
                ReplaceWith     = replaceWith,
                Prefix          = prefix,
                IncludePatterns = ParsePatterns(el.Attribute("Include")?.Value),
                ExcludePatterns = ParsePatterns(el.Attribute("Exclude")?.Value),
            });
        }

        // InitMethodName must be unique across Dynamic-mode libraries
        var duplicates = specs
            .Where(s => s.Mode == LibraryMode.Dynamic)
            .GroupBy(s => s.InitMethodName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new WeavingException(
                "[LazyImport.Fody] Multiple <Library> entries share the same InitMethod name (must be unique): " +
                string.Join(", ", duplicates));

        LogSpecs(specs);
        return specs;
    }

    /// <summary>
    /// Auto-scans all P/Invoke methods in the module for referenced DLLs when no &lt;Library&gt; config is present.
    /// A Dynamic spec is created automatically only when all methods reference the same DLL; otherwise throws.
    /// </summary>
    private List<LibrarySpec> AutoDetectSpecs()
    {
        var dllNames = AllTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.IsPInvokeImpl && m.HasPInvokeInfo)
            .Select(m => StripExtension(m.PInvokeInfo.Module.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dllNames.Count == 0)
        {
            WriteWarning("[LazyImport.Fody] No P/Invoke methods found"); 
            return new List<LibrarySpec>();
        }

        if (dllNames.Count > 1)
            throw new WeavingException(
                "[LazyImport.Fody] Multiple native libraries found (" +
                string.Join(", ", dllNames.Select(n => $"\"{n}\"")) +
                "). Please add explicit <Library> elements to configure them.");

        var spec = new LibrarySpec
        {
            Name           = dllNames[0],
            Mode           = LibraryMode.Dynamic,
            InitMethodName = "Initialize",
            Prefix         = NormalizeName(dllNames[0]),
        };

        WriteInfo($"[LazyImport.Fody] Auto-detected library \"{spec.Name}\", using Dynamic mode + Initialize.");
        return new List<LibrarySpec> { spec };
    }

    private void LogSpecs(List<LibrarySpec> specs)
    {
        WriteInfo($"[LazyImport.Fody] Parsed {specs.Count} library spec(s):");
        foreach (var s in specs)
        {
            var filter = FormatFilter(s);
            if (s.Mode == LibraryMode.Static)
                WriteInfo($"  [Static]  \"{s.Name}\" → ReplaceName=\"{s.ReplaceWith}\"{filter}");
            else
                WriteInfo($"  [Dynamic] \"{s.Name}\" → InitMethod=\"{s.InitMethodName}\"{filter}");
        }
    }

    private static string FormatFilter(LibrarySpec s)
    {
        var parts = new List<string>(2);
        if (s.IncludePatterns.Length > 0) parts.Add($"Include={s.IncludePatterns.Length} pattern(s)");
        if (s.ExcludePatterns.Length > 0) parts.Add($"Exclude={s.ExcludePatterns.Length} pattern(s)");
        return parts.Count > 0 ? "  [" + string.Join(", ", parts) + "]" : "";
    }

    // ════════════════════════════════════════════════════════════════════════
    // STATIC MODE
    // ════════════════════════════════════════════════════════════════════════

    private void ExecuteStatic(List<LibrarySpec> specs)
    {
        foreach (var spec in specs)
        {
            WriteInfo($"[LazyImport.Fody/Static] Replacing DllImport(\"{spec.Name}\") " +
                      $"→ (\"{spec.ReplaceWith}\"){FormatFilter(spec)}");

            var count = 0;
            foreach (var type in AllTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.IsPInvokeImpl || !method.HasPInvokeInfo) continue;

                    if (!StripExtension(method.PInvokeInfo.Module.Name)
                            .Equals(spec.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!IsMethodIncluded(spec, method.Name)) continue;

                    // Reuse an existing ModuleReference for ReplaceWith to avoid duplicate entries
                    var newRef = ModuleDefinition.ModuleReferences
                        .FirstOrDefault(r => r.Name == spec.ReplaceWith);
                    if (newRef is null)
                    {
                        newRef = new ModuleReference(spec.ReplaceWith);
                        ModuleDefinition.ModuleReferences.Add(newRef);
                    }

                    // Preserve all PInvokeInfo settings, changing only Module
                    method.PInvokeInfo = new PInvokeInfo(
                        method.PInvokeInfo.Attributes,
                        method.PInvokeInfo.EntryPoint,
                        newRef);

                    // Sync [DllImport] CustomAttribute constructor argument so reflection sees the new name
                    var dllAttr = method.CustomAttributes
                        .FirstOrDefault(a => a.AttributeType.Name == "DllImportAttribute");
                    if (dllAttr?.ConstructorArguments.Count > 0)
                    {
                        var argType = dllAttr.ConstructorArguments[0].Type;
                        dllAttr.ConstructorArguments[0] =
                            new CustomAttributeArgument(argType, spec.ReplaceWith);
                    }

                    count++;
                    WriteInfo($"  {type.FullName}::{method.Name}  → \"{spec.ReplaceWith}\"");
                }
            }

            WriteInfo($"[LazyImport.Fody/Static] \"{spec.Name}\" — {count} method(s) replaced.");
        }

        CleanUnusedModuleReferences();
        WriteInfo("[LazyImport.Fody/Static] Done.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // DYNAMIC MODE
    // ════════════════════════════════════════════════════════════════════════

    private void ExecuteDynamic(List<LibrarySpec> specs)
    {
        var libNames = string.Join(", ", specs.Select(s => $"\"{s.Name}\""));
        WriteInfo($"[LazyImport.Fody/Dynamic] Processing {libNames} using delegate-based dynamic loading " +
                  "(function pointers only for methods with delegate* unmanaged parameters).");

        var typesTransformed = 0;
        foreach (var type in AllTypes().ToList())
        {
            if (ProcessType(type, specs))
                typesTransformed++;
        }

        WriteInfo($"[LazyImport.Fody/Dynamic] Done — {typesTransformed} type(s) transformed.");
    }

    // ── Common type imports ──────────────────────────────────────────────────

    private void ImportCommonTypes()
    {
        _voidRef              = ModuleDefinition.ImportReference(typeof(void));
        _intPtrRef            = ModuleDefinition.ImportReference(typeof(IntPtr));
        _stringRef            = ModuleDefinition.ImportReference(typeof(string));
        _objectRef            = ModuleDefinition.ImportReference(typeof(object));
        _multicastDelegateRef = ModuleDefinition.ImportReference(typeof(MulticastDelegate));
        _asyncResultRef       = ModuleDefinition.ImportReference(typeof(IAsyncResult));
        _asyncCallbackRef     = ModuleDefinition.ImportReference(typeof(AsyncCallback));

        var marshalTypeDef = FindTypeDefinition("System.Runtime.InteropServices.Marshal")
            ?? throw new WeavingException(
                "[LazyImport.Fody] Cannot find System.Runtime.InteropServices.Marshal in target assembly references.");

        // Get generic overload GetDelegateForFunctionPointer<T>(IntPtr)
        var getDfp = marshalTypeDef.Methods.First(m =>
            m.Name == "GetDelegateForFunctionPointer" &&
            m.HasGenericParameters &&
            m.Parameters.Count == 1);
        _marshalGetDfpOpen = ModuleDefinition.ImportReference(getDfp);
    }

    // ── Native loader resolution / injection ───────────────────────────────

    private void ResolveOrInjectNativeLoader()
    {
        // Prefer an existing NativeLibraryLoader class in the target assembly
        var loaderType = ModuleDefinition.Types
            .FirstOrDefault(t => t.Name == "NativeLibraryLoader");

        if (loaderType != null)
        {
            _nativeLoad = ModuleDefinition.ImportReference(
                loaderType.Methods.First(m => m.Name == "Load" && m.Parameters.Count == 1));
            _nativeGetExport = ModuleDefinition.ImportReference(
                loaderType.Methods.First(m => m.Name == "GetExport" && m.Parameters.Count == 2));
            WriteInfo("[LazyImport.Fody/Dynamic] Using existing NativeLibraryLoader.");
            return;
        }

        // Check if .NET 5+ NativeLibrary is available
        var hasNativeLib = TryFindTypeDefinition("System.Runtime.InteropServices.NativeLibrary", out var nativeLibType);

        loaderType       = InjectLoader(hasNativeLib ? nativeLibType : null);
        _nativeLoad      = ModuleDefinition.ImportReference(loaderType.Methods.First(m => m.Name == "Load"));
        _nativeGetExport = ModuleDefinition.ImportReference(loaderType.Methods.First(m => m.Name == "GetExport"));
    }

    /// <summary>
    /// Injects the runtime-platform-aware native library loader helper class <c>__FodyDynamicLoader</c>.
    /// <list type="bullet">
    ///   <item><paramref name="nativeLibType"/> non-null (.NET 5+) → delegates to NativeLibrary.</item>
    ///   <item>Otherwise → injects a multi-platform loader with runtime branches (Win32 API + libdl).</item>
    /// </list>
    /// </summary>
    private TypeDefinition InjectLoader(TypeDefinition? nativeLibType)
    {
        if (nativeLibType != null)
        {
            WriteInfo("[LazyImport.Fody/Dynamic] Injecting __FodyDynamicLoader (NativeLibrary-based, .NET 5+).");
            return InjectLoaderNet5(nativeLibType);
        }

        WriteInfo("[LazyImport.Fody/Dynamic] Injecting __FodyDynamicLoader (multi-platform P/Invoke fallback).");
        return InjectLoaderLegacy();
    }

    // ── .NET 5+ loader injection ─────────────────────────────────────────────

    /// <summary>Injects the loader using .NET 5+ NativeLibrary.</summary>
    private TypeDefinition InjectLoaderNet5(TypeDefinition nativeLibType)
    {
        var nativeLibLoad = ModuleDefinition.ImportReference(
            nativeLibType.Methods.First(m =>
                m.Name == "Load" &&
                m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.String"));

        var nativeLibGetExport = ModuleDefinition.ImportReference(
            nativeLibType.Methods.First(m =>
                m.Name == "GetExport" &&
                m.Parameters.Count == 2 &&
                m.Parameters[0].ParameterType.FullName == "System.IntPtr" &&
                m.Parameters[1].ParameterType.FullName == "System.String"));

        var loaderType = CreateLoaderTypeSkeleton();

        // public static IntPtr Load(string path) => NativeLibrary.Load(path);
        var loadMethod = NewStaticMethod("Load", _intPtrRef, ("path", _stringRef));
        var loadIl = InitMethodBody(loadMethod);
        loadIl.Emit(OpCodes.Ldarg_0);
        loadIl.Emit(OpCodes.Call, nativeLibLoad);
        loadIl.Emit(OpCodes.Ret);
        loaderType.Methods.Add(loadMethod);

        // public static IntPtr GetExport(IntPtr handle, string symbol) => NativeLibrary.GetExport(handle, symbol);
        var getExportMethod = NewStaticMethod("GetExport", _intPtrRef,
            ("handle", _intPtrRef), ("symbol", _stringRef));
        var getIl = InitMethodBody(getExportMethod);
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldarg_1);
        getIl.Emit(OpCodes.Call, nativeLibGetExport);
        getIl.Emit(OpCodes.Ret);
        loaderType.Methods.Add(getExportMethod);

        ModuleDefinition.Types.Add(loaderType);
        return loaderType;
    }

    // ── Legacy multi-platform loader injection ──────────────────────────────

    /// <summary>
    /// Injects a loader with cross-platform branches for legacy .NET (Standard 2.0 / Framework 4.x).
    ///
    /// Equivalent C#:
    /// <code><![CDATA[
    /// static class __FodyDynamicLoader {
    ///     [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern IntPtr LoadLibraryW(string p);
    ///     [DllImport("kernel32")] static extern IntPtr GetProcAddress(IntPtr h, string s);
    ///     [DllImport("libdl", CallingConvention=Cdecl)] static extern IntPtr dlopen(string p, int flags);
    ///     [DllImport("libdl", CallingConvention=Cdecl)] static extern IntPtr dlsym(IntPtr h, string s);
    ///
    ///     public static IntPtr Load(string path) {
    ///         if (IsWindows()) return LoadLibraryW(path);
    ///         return dlopen(path, 2 /*RTLD_NOW*/);
    ///     }
    ///     public static IntPtr GetExport(IntPtr handle, string symbol) {
    ///         if (IsWindows()) return GetProcAddress(handle, symbol);
    ///         return dlsym(handle, symbol);
    ///     }
    ///     static bool IsWindows() { ... }
    /// }
    /// ]]></code>
    /// </summary>
    private TypeDefinition InjectLoaderLegacy()
    {
        var intRef     = ModuleDefinition.ImportReference(typeof(int));
        var loaderType = CreateLoaderTypeSkeleton();

        // ── Windows P/Invoke ─────────────────────────────────────────────────
        var kernel32Ref = new ModuleReference("kernel32");
        ModuleDefinition.ModuleReferences.Add(kernel32Ref);

        // LoadLibraryW(string):IntPtr — Unicode encoding
        var loadLibW = NewPInvokeMethod("LoadLibraryW", _intPtrRef, kernel32Ref, "LoadLibraryW",
            PInvokeAttributes.CallConvWinapi | PInvokeAttributes.CharSetUnicode | PInvokeAttributes.NoMangle,
            ("lpFileName", _stringRef));
        loaderType.Methods.Add(loadLibW);

        // GetProcAddress(IntPtr, string):IntPtr — ANSI encoding
        var getProcAddr = NewPInvokeMethod("GetProcAddress", _intPtrRef, kernel32Ref, "GetProcAddress",
            PInvokeAttributes.CallConvWinapi | PInvokeAttributes.CharSetAnsi | PInvokeAttributes.NoMangle,
            ("hModule", _intPtrRef), ("lpProcName", _stringRef));
        loaderType.Methods.Add(getProcAddr);

        // ── Unix/macOS P/Invoke ──────────────────────────────────────────────
        // Linux uses libdl.so.2; macOS before 10.15 uses libdl (same name)
        var libdlRef = new ModuleReference("libdl");
        ModuleDefinition.ModuleReferences.Add(libdlRef);

        // dlopen(string, int):IntPtr — RTLD_NOW=2 resolves all symbols immediately
        var dlopen = NewPInvokeMethod("dlopen", _intPtrRef, libdlRef, "dlopen",
            PInvokeAttributes.CallConvCdecl | PInvokeAttributes.CharSetAnsi | PInvokeAttributes.NoMangle,
            ("filename", _stringRef), ("flags", intRef));
        loaderType.Methods.Add(dlopen);

        // dlsym(IntPtr, string):IntPtr
        var dlsym = NewPInvokeMethod("dlsym", _intPtrRef, libdlRef, "dlsym",
            PInvokeAttributes.CallConvCdecl | PInvokeAttributes.CharSetAnsi | PInvokeAttributes.NoMangle,
            ("handle", _intPtrRef), ("symbol", _stringRef));
        loaderType.Methods.Add(dlsym);

        // ── IsWindows helper method ──────────────────────────────────────────
        var boolRef = ModuleDefinition.ImportReference(typeof(bool));
        var isWindowsMethod = BuildIsWindowsMethod(boolRef);
        loaderType.Methods.Add(isWindowsMethod);

        // ── public static IntPtr Load(string path) ───────────────────────────
        // if (IsWindows()) return LoadLibraryW(path);
        // return dlopen(path, 2 /*RTLD_NOW*/);
        var loadMethod = NewStaticMethod("Load", _intPtrRef, ("path", _stringRef));
        var loadIl = InitMethodBody(loadMethod);
        var loadElse = loadIl.Create(OpCodes.Ldarg_0); // else branch: load path argument
        loadIl.Emit(OpCodes.Call, isWindowsMethod);
        loadIl.Emit(OpCodes.Brfalse_S, loadElse);
        loadIl.Emit(OpCodes.Ldarg_0);
        loadIl.Emit(OpCodes.Call, loadLibW);
        loadIl.Emit(OpCodes.Ret);
        loadIl.Append(loadElse);
        loadIl.Emit(OpCodes.Ldc_I4_2); // RTLD_NOW
        loadIl.Emit(OpCodes.Call, dlopen);
        loadIl.Emit(OpCodes.Ret);
        loaderType.Methods.Add(loadMethod);

        // ── public static IntPtr GetExport(IntPtr handle, string symbol) ─────
        // if (IsWindows()) return GetProcAddress(handle, symbol);
        // return dlsym(handle, symbol);
        var getExportMethod = NewStaticMethod("GetExport", _intPtrRef,
            ("handle", _intPtrRef), ("symbol", _stringRef));
        var getIl = InitMethodBody(getExportMethod);
        var getElse = getIl.Create(OpCodes.Ldarg_0); // else branch: load handle argument
        getIl.Emit(OpCodes.Call, isWindowsMethod);
        getIl.Emit(OpCodes.Brfalse_S, getElse);
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldarg_1);
        getIl.Emit(OpCodes.Call, getProcAddr);
        getIl.Emit(OpCodes.Ret);
        getIl.Append(getElse);
        getIl.Emit(OpCodes.Ldarg_1);
        getIl.Emit(OpCodes.Call, dlsym);
        getIl.Emit(OpCodes.Ret);
        loaderType.Methods.Add(getExportMethod);

        ModuleDefinition.Types.Add(loaderType);
        return loaderType;
    }

    /// <summary>
    /// Generates the private <c>IsWindows():bool</c> helper method.
    /// Prefers <c>RuntimeInformation.IsOSPlatform(OSPlatform.Windows)</c> (.NET Standard 1.1+),
    /// falling back to <c>Environment.OSVersion.Platform == PlatformID.Win32NT</c> (.NET Framework).
    /// </summary>
    private MethodDefinition BuildIsWindowsMethod(TypeReference boolRef)
    {
        var method = new MethodDefinition(
            "IsWindows",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            boolRef);
        var il = InitMethodBody(method);

        // Try RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        var rtInfoType     = FindTypeDefinition("System.Runtime.InteropServices.RuntimeInformation");
        var osPlatformType = FindTypeDefinition("System.Runtime.InteropServices.OSPlatform");

        if (rtInfoType != null && osPlatformType != null)
        {
            var windowsProp      = osPlatformType.Properties.FirstOrDefault(p => p.Name == "Windows");
            var isOsPlatformMeth = rtInfoType.Methods
                .FirstOrDefault(m => m.Name == "IsOSPlatform" && m.Parameters.Count == 1);

            if (windowsProp?.GetMethod != null && isOsPlatformMeth != null)
            {
                // return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                il.Emit(OpCodes.Call, ModuleDefinition.ImportReference(windowsProp.GetMethod));
                il.Emit(OpCodes.Call, ModuleDefinition.ImportReference(isOsPlatformMeth));
                il.Emit(OpCodes.Ret);
                return method;
            }
        }

        // Fallback: return (Environment.OSVersion.Platform == PlatformID.Win32NT)
        // PlatformID.Win32NT = 2
        var envType = FindTypeDefinition("System.Environment")
            ?? throw new WeavingException(
                "[LazyImport.Fody] Cannot find System.Environment; cannot inject cross-platform loader.");
        var osVersionProp = envType.Properties.First(p => p.Name == "OSVersion");
        var osVersionType = osVersionProp.PropertyType.Resolve();
        var platformProp  = osVersionType.Properties.First(p => p.Name == "Platform");

        il.Emit(OpCodes.Call, ModuleDefinition.ImportReference(osVersionProp.GetMethod));
        il.Emit(OpCodes.Callvirt, ModuleDefinition.ImportReference(platformProp.GetMethod));
        il.Emit(OpCodes.Ldc_I4_2); // PlatformID.Win32NT == 2
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);
        return method;
    }

    // ── Loader helper creation methods ──────────────────────────────────────

    /// <summary>Creates the type skeleton for __FodyDynamicLoader (sealed static class).</summary>
    private TypeDefinition CreateLoaderTypeSkeleton() =>
        new TypeDefinition(
            string.Empty,
            "__FodyDynamicLoader",
            TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract |
            TypeAttributes.BeforeFieldInit,
            ModuleDefinition.ImportReference(typeof(object)));

    /// <summary>Creates a private static extern method definition with PInvokeInfo.</summary>
    private MethodDefinition NewPInvokeMethod(
        string name,
        TypeReference returnType,
        ModuleReference moduleRef,
        string entryPoint,
        PInvokeAttributes attrs,
        params (string paramName, TypeReference paramType)[] parameters)
    {
        var method = new MethodDefinition(
            name,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
            MethodAttributes.PInvokeImpl,
            returnType);
        method.IsPInvokeImpl = true;
        method.ImplAttributes = MethodImplAttributes.PreserveSig;
        method.PInvokeInfo = new PInvokeInfo(attrs, entryPoint, moduleRef);
        foreach (var (pName, pType) in parameters)
            method.Parameters.Add(new ParameterDefinition(pName, ParameterAttributes.None, pType));
        return method;
    }

    /// <summary>Creates a public static method skeleton (body not yet filled).</summary>
    private MethodDefinition NewStaticMethod(
        string name,
        TypeReference returnType,
        params (string paramName, TypeReference paramType)[] parameters)
    {
        var method = new MethodDefinition(
            name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            returnType);
        foreach (var (pName, pType) in parameters)
            method.Parameters.Add(new ParameterDefinition(pName, ParameterAttributes.None, pType));
        return method;
    }

    /// <summary>Allocates a new method body and returns the ILProcessor.</summary>
    private static ILProcessor InitMethodBody(MethodDefinition method)
    {
        method.Body = new MethodBody(method);
        return method.Body.GetILProcessor();
    }

    // ── Per-type transformation ──────────────────────────────────────────────

    /// <summary>
    /// Transforms all extern methods in <paramref name="type"/> that match a Dynamic spec.
    /// <list type="number">
    ///   <item>Creates the <c>__libraryHandle_{prefix}</c> static field.</item>
    ///   <item>Creates a binding per extern method (function pointer field or delegate type + field).</item>
    ///   <item>Injects Initialize(IntPtr) / Initialize(string) methods.</item>
    ///   <item>Replaces each extern method body with function-pointer calli or delegate callvirt IL.</item>
    /// </list>
    /// </summary>
    private bool ProcessType(TypeDefinition type, List<LibrarySpec> specs)
    {
        var anyTransformed = false;

        foreach (var spec in specs)
        {
            // Collect extern methods in this type that belong to this library and pass the filter
            var externMethods = type.Methods
                .Where(m => m.IsStatic && m.IsPInvokeImpl && m.HasPInvokeInfo &&
                            StripExtension(m.PInvokeInfo.Module.Name)
                                .Equals(spec.Name, StringComparison.OrdinalIgnoreCase) &&
                            IsMethodIncluded(spec, m.Name))
                .ToList();

            if (externMethods.Count == 0) continue;

            anyTransformed = true;
            WriteInfo($"[LazyImport.Fody/Dynamic] Transforming {type.FullName} " +
                      $"({externMethods.Count} extern(s) from \"{spec.Name}\"{FormatFilter(spec)}).");

            // Handle field: stores the loaded library handle
            var handleField = new FieldDefinition(
                $"__libraryHandle_{spec.Prefix}",
                FieldAttributes.Private | FieldAttributes.Static,
                _intPtrRef);
            type.Fields.Add(handleField);

            // Per-method: build external function binding descriptors
            var bindings = new List<ExternBinding>();
            foreach (var extern_ in externMethods)
            {
                // Compute effective parameter list (handle variadic args)
                var effectiveParams = GetEffectiveParameters(extern_);

                // Check if the original method uses delegate* unmanaged in its signature
                var usesFunctionPointer = ContainsFunctionPointerType(extern_);

                ExternBinding binding;
                if (usesFunctionPointer)
                {
                    // Function pointer path: only when original signature contains delegate* unmanaged
                    WriteInfo($"  {extern_.Name} uses delegate* unmanaged → function pointer path");

                    var funcPtrType = CreateFunctionPointerType(extern_, effectiveParams);
                    var fpField = new FieldDefinition(
                        $"__fn_{spec.Prefix}_{extern_.Name}",
                        FieldAttributes.Private | FieldAttributes.Static,
                        funcPtrType);
                    type.Fields.Add(fpField);

                    binding = new ExternBinding
                    {
                        Extern              = extern_,
                        BackingField        = fpField,
                        FuncPtrType         = funcPtrType,
                        EffectiveParameters = effectiveParams,
                    };
                }
                else
                {
                    // Delegate path: default for all other cases (preserves MarshalInfo)
                    var delegateType = CreateDelegateType(
                        extern_, $"__delegate_{spec.Prefix}_{extern_.Name}", effectiveParams);
                    type.NestedTypes.Add(delegateType);

                    var delField = new FieldDefinition(
                        $"__fn_{spec.Prefix}_{extern_.Name}",
                        FieldAttributes.Private | FieldAttributes.Static,
                        delegateType);
                    type.Fields.Add(delField);

                    binding = new ExternBinding
                    {
                        Extern              = extern_,
                        BackingField        = delField,
                        DelegateType        = delegateType,
                        EffectiveParameters = effectiveParams,
                    };
                }

                bindings.Add(binding);
            }

            if (bindings.Count == 0) continue;

            GenerateInitializeMethod(type, spec.InitMethodName, handleField, bindings);

            foreach (var binding in bindings)
                ReplaceExternBody(binding);
        }

        return anyTransformed;
    }

    // ── Effective parameter computation (variadic handling) ─────────────────

    /// <summary>
    /// Determines whether a method signature contains delegate* unmanaged types
    /// (in parameters or return type), requiring function pointer path.
    /// </summary>
    private static bool ContainsFunctionPointerType(MethodDefinition method)
    {
        // Check return type
        if (method.ReturnType is FunctionPointerType)
            return true;

        // Check parameters
        foreach (var param in method.Parameters)
        {
            if (param.ParameterType is FunctionPointerType)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Computes the effective parameter list for an extern method:
    /// <list type="bullet">
    ///   <item><c>VarArg</c> calling convention: keep all fixed parameters (variadic part cannot be enumerated, ignored).</item>
    ///   <item>Last parameter carrying <c>ParamArrayAttribute</c> (C# <c>params</c>):
    ///     replaced with an <c>IntPtr</c> parameter; the caller passes a raw pointer.</item>
    ///   <item>Otherwise: returned as-is.</item>
    /// </list>
    /// </summary>
    private IReadOnlyList<ParameterDefinition> GetEffectiveParameters(MethodDefinition method)
    {
        var parameters = method.Parameters;

        if (method.CallingConvention == MethodCallingConvention.VarArg)
        {
            // Variadic: use only fixed parameters; calling convention set to Cdecl during delegate/FP creation
            WriteInfo($"[LazyImport.Fody/Dynamic] {method.DeclaringType?.Name}::{method.Name}" +
                         " — VarArg method: fixed parameters extracted, variadic part ignored.");
            return parameters.ToList();
        }

        if (parameters.Count > 0)
        {
            var last = parameters[parameters.Count - 1];
            if (last.CustomAttributes.Any(a => a.AttributeType.Name == "ParamArrayAttribute"))
            {
                // params T[]: replace with IntPtr (caller passes a C-style variadic pointer)
                WriteInfo($"[LazyImport.Fody/Dynamic] {method.DeclaringType?.Name}::{method.Name}" +
                             $" — params parameter '{last.Name}' replaced with IntPtr.");
                var effective = parameters.Take(parameters.Count - 1).ToList();
                effective.Add(new ParameterDefinition(
                    last.Name, ParameterAttributes.None, _intPtrRef));
                return effective;
            }
        }

        return parameters.ToList();
    }

    // ── .NET 5+ function pointer type creation ───────────────────────────────

    /// <summary>
    /// Creates a <see cref="FunctionPointerType"/> matching the signature of <paramref name="extern_"/>.
    /// The calling convention is mapped from <see cref="PInvokeInfo"/>; VarArg methods always use Cdecl.
    /// </summary>
    private FunctionPointerType CreateFunctionPointerType(
        MethodDefinition extern_,
        IReadOnlyList<ParameterDefinition> effectiveParams)
    {
        var funcPtrType = new FunctionPointerType
        {
            ReturnType        = ModuleDefinition.ImportReference(extern_.ReturnType),
            CallingConvention = GetFuncPtrCallingConvention(extern_),
        };

        // Copy parameters (types only, no marshal info; calli relies on caller ensuring consistent layout)
        foreach (var p in effectiveParams)
            funcPtrType.Parameters.Add(new ParameterDefinition(
                p.Name, ParameterAttributes.None,
                ModuleDefinition.ImportReference(p.ParameterType)));

        return funcPtrType;
    }

    /// <summary>
    /// Maps the calling convention from <see cref="PInvokeInfo"/> to the
    /// <see cref="MethodCallingConvention"/> used by Cecil function pointers.
    /// VarArg and Winapi are both mapped to Cdecl (safest cross-platform default).
    /// </summary>
    private static MethodCallingConvention GetFuncPtrCallingConvention(MethodDefinition method)
    {
        // VarArg methods always use Cdecl (C-style variadic convention)
        if (method.CallingConvention == MethodCallingConvention.VarArg)
            return MethodCallingConvention.C;

        if (!method.HasPInvokeInfo) return MethodCallingConvention.Default;

        var info = method.PInvokeInfo;
        if (info.IsCallConvCdecl)    return MethodCallingConvention.C;
        if (info.IsCallConvStdCall)  return MethodCallingConvention.StdCall;
        if (info.IsCallConvFastcall) return MethodCallingConvention.FastCall;
        if (info.IsCallConvThiscall) return MethodCallingConvention.ThisCall;
        // Winapi (Windows default): StdCall on x86, platform default on x64/ARM;
        // Cdecl is safer on modern ABIs and used as the fallback here
        return MethodCallingConvention.C;
    }

    // ── Legacy delegate type construction ───────────────────────────────────

    /// <summary>
    /// Creates a private nested delegate type for the legacy path with a signature matching the extern method.
    /// <list type="bullet">
    ///   <item>Adds <c>[UnmanagedFunctionPointer(cc)]</c> based on calling convention.</item>
    ///   <item>Copies <c>MarshalInfo</c> and In/Out marshaling attributes from the original method parameters.</item>
    ///   <item>Copies <c>MethodReturnType.MarshalInfo</c> from the original method return type.</item>
    ///   <item>VarArg calling convention delegates use Cdecl.</item>
    /// </list>
    /// </summary>
    private TypeDefinition CreateDelegateType(
        MethodDefinition extern_,
        string delegateName,
        IReadOnlyList<ParameterDefinition> effectiveParams)
    {
        var dt = new TypeDefinition(
            string.Empty,
            delegateName,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            _multicastDelegateRef);

        // .ctor(object, native int) — implementation provided by the runtime
        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            _voidRef);
        ctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, _objectRef));
        ctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, _intPtrRef));
        ctor.ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
        dt.Methods.Add(ctor);

        var returnRef = ModuleDefinition.ImportReference(extern_.ReturnType);

        // Invoke(…) — matches the effective extern signature, marshal attributes copied
        var invoke = new MethodDefinition(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.Virtual | MethodAttributes.NewSlot,
            returnRef);

        // Copy return value MarshalInfo
        if (extern_.MethodReturnType.HasMarshalInfo)
            invoke.MethodReturnType.MarshalInfo = CloneMarshalInfo(extern_.MethodReturnType.MarshalInfo);

        // Copy parameters (including In/Out attributes and MarshalInfo)
        foreach (var p in effectiveParams)
        {
            var np = new ParameterDefinition(
                p.Name,
                p.Attributes, // preserve In/Out/Optional flags
                ModuleDefinition.ImportReference(p.ParameterType));
            if (p.HasMarshalInfo)
                np.MarshalInfo = CloneMarshalInfo(p.MarshalInfo);
            invoke.Parameters.Add(np);
        }

        invoke.ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
        dt.Methods.Add(invoke);

        // BeginInvoke — implementation provided by the runtime
        var beginInvoke = new MethodDefinition(
            "BeginInvoke",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.Virtual | MethodAttributes.NewSlot,
            _asyncResultRef);
        foreach (var p in effectiveParams)
            beginInvoke.Parameters.Add(new ParameterDefinition(
                p.Name, p.Attributes, ModuleDefinition.ImportReference(p.ParameterType)));
        beginInvoke.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, _asyncCallbackRef));
        beginInvoke.Parameters.Add(new ParameterDefinition("object",   ParameterAttributes.None, _objectRef));
        beginInvoke.ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
        dt.Methods.Add(beginInvoke);

        // EndInvoke — implementation provided by the runtime
        var endInvoke = new MethodDefinition(
            "EndInvoke",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.Virtual | MethodAttributes.NewSlot,
            returnRef);
        endInvoke.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.None, _asyncResultRef));
        endInvoke.ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
        dt.Methods.Add(endInvoke);

        // Add [UnmanagedFunctionPointer] based on calling convention (VarArg → Cdecl)
        var cc = GetDelegatCallingConvention(extern_);
        if (cc.HasValue)
            AddUnmanagedFunctionPointerAttr(dt, cc.Value);

        return dt;
    }

    /// <summary>
    /// Maps the extern method's calling convention to <see cref="CallingConvention"/> (for delegate attributes).
    /// VarArg is treated as Cdecl; Winapi and default return null (no attribute added).
    /// </summary>
    private static CallingConvention? GetDelegatCallingConvention(MethodDefinition extern_)
    {
        // VarArg methods always use Cdecl
        if (extern_.CallingConvention == MethodCallingConvention.VarArg)
            return CallingConvention.Cdecl;

        if (!extern_.HasPInvokeInfo) return null;
        return GetCallingConvention(extern_.PInvokeInfo);
    }

    /// <summary>
    /// Maps the calling convention flags from <see cref="PInvokeInfo"/> to a
    /// <see cref="CallingConvention"/> enum value (used for the UnmanagedFunctionPointer attribute).
    /// </summary>
    private static CallingConvention? GetCallingConvention(PInvokeInfo info)
    {
        if (info.IsCallConvCdecl)    return CallingConvention.Cdecl;
        if (info.IsCallConvStdCall)  return CallingConvention.StdCall;
        if (info.IsCallConvFastcall) return CallingConvention.FastCall;
        if (info.IsCallConvThiscall) return CallingConvention.ThisCall;
        if (info.IsCallConvWinapi)   return CallingConvention.Winapi;
        return null; // default calling convention, no attribute needed
    }

    /// <summary>Adds the [UnmanagedFunctionPointer(cc)] custom attribute to the delegate type.</summary>
    private void AddUnmanagedFunctionPointerAttr(TypeDefinition dt, CallingConvention cc)
    {
        var ufpAttrDef = FindTypeDefinition(
            "System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute");
        var ccDef = FindTypeDefinition(
            "System.Runtime.InteropServices.CallingConvention");
        if (ufpAttrDef == null || ccDef == null) return;

        var ufpCtor = ModuleDefinition.ImportReference(
            ufpAttrDef.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1));
        var attr = new CustomAttribute(ufpCtor);
        attr.ConstructorArguments.Add(
            new CustomAttributeArgument(ModuleDefinition.ImportReference(ccDef), cc));
        dt.CustomAttributes.Add(attr);
    }

    /// <summary>
    /// Clones a MarshalInfo instance, importing any TypeReferences it contains (e.g., CustomMarshalInfo.ManagedType).
    /// This ensures that custom marshaler types are properly imported into the target module.
    /// </summary>
    private MarshalInfo CloneMarshalInfo(MarshalInfo original)
    {
        switch (original)
        {
            case CustomMarshalInfo custom:
                var cloned = new CustomMarshalInfo
                {
                    Guid = custom.Guid,
                    Cookie = custom.Cookie
                };
                // Import the custom marshaler type reference
                if (custom.ManagedType != null)
                    cloned.ManagedType = ModuleDefinition.ImportReference(custom.ManagedType);
                return cloned;

            case ArrayMarshalInfo array:
                var clonedArray = new ArrayMarshalInfo
                {
                    ElementType = array.ElementType,
                    Size = array.Size,
                    SizeParameterIndex = array.SizeParameterIndex,
                    SizeParameterMultiplier = array.SizeParameterMultiplier
                };
                return clonedArray;

            case FixedArrayMarshalInfo fixedArray:
                var clonedFixed = new FixedArrayMarshalInfo
                {
                    ElementType = fixedArray.ElementType,
                    Size = fixedArray.Size
                };
                return clonedFixed;

            case SafeArrayMarshalInfo safeArray:
                var clonedSafe = new SafeArrayMarshalInfo
                {
                    ElementType = safeArray.ElementType
                };
                return clonedSafe;

            case FixedSysStringMarshalInfo fixedSys:
                return new FixedSysStringMarshalInfo { Size = fixedSys.Size };

            default:
                // For simple MarshalInfo (e.g., LPStr, LPWStr, Bool), direct copy is sufficient
                return original;
        }
    }

    // ── Initialize method injection ──────────────────────────────────────────

    /// <summary>
    /// Generates two Initialize overloads for each library:
    /// <list type="bullet">
    ///   <item><c>Initialize(IntPtr libraryHandle)</c> — primary overload: saves the handle and resolves all function pointers.</item>
    ///   <item><c>Initialize(string libraryPath)</c> — thin wrapper: calls Load(path) then forwards to Initialize(IntPtr).</item>
    /// </list>
    /// If a stub method with a matching signature already exists in the type, its body is replaced instead of adding a new method.
    /// </summary>
    private void GenerateInitializeMethod(
        TypeDefinition type,
        string initMethodName,
        FieldDefinition handleField,
        List<ExternBinding> bindings)
    {
        // Generate IntPtr overload first (full logic), then string wrapper overload (references IntPtr overload)
        GenerateInitializeOverload(type, initMethodName, handleField, bindings, useHandle: true);
        GenerateInitializeOverload(type, initMethodName, handleField, bindings, useHandle: false);
    }

    private void GenerateInitializeOverload(
        TypeDefinition type,
        string initMethodName,
        FieldDefinition handleField,
        List<ExternBinding> bindings,
        bool useHandle)
    {
        var paramType      = useHandle ? _intPtrRef  : _stringRef;
        var paramName      = useHandle ? "libraryHandle" : "libraryPath";
        var paramFullName  = useHandle ? "System.IntPtr"  : "System.String";
        var paramLabel     = useHandle ? "IntPtr" : "string";

        // Replace the body of an existing stub with matching name and parameter type; otherwise create a new method
        var stub = type.Methods.FirstOrDefault(m =>
            m.Name == initMethodName &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == paramFullName);

        MethodDefinition init;
        if (stub != null)
        {
            WriteInfo($"[LazyImport.Fody/Dynamic] Replacing existing stub {initMethodName}({paramLabel}) in {type.FullName}.");
            init = stub;
        }
        else
        {
            init = new MethodDefinition(
                initMethodName,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                _voidRef);
            init.Parameters.Add(new ParameterDefinition(paramName, ParameterAttributes.None, paramType));
            type.Methods.Add(init);
        }

        var body = init.Body = new MethodBody(init);
        var il   = body.GetILProcessor();

        if (!useHandle)
        {
            // string overload: Initialize(NativeLoader.Load(libraryPath))
            var handleOverload = type.Methods.First(m =>
                m.Name == initMethodName &&
                m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.IntPtr");

            il.Emit(OpCodes.Ldarg_0);              // libraryPath
            il.Emit(OpCodes.Call, _nativeLoad);    // Load(string) → IntPtr
            il.Emit(OpCodes.Call, handleOverload); // Initialize(IntPtr)
        }
        else
        {
            // IntPtr overload: save handle and resolve function pointers one by one
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stsfld, handleField); // __libraryHandle_{prefix} = handle

            foreach (var binding in bindings)
            {
                // Get function export address (IntPtr)
                // Use EntryPoint from PInvokeInfo if available, otherwise fall back to method name
                var entryPoint = binding.Extern.PInvokeInfo?.EntryPoint ?? binding.Extern.Name;
                il.Emit(OpCodes.Ldarg_0);                      // handle
                il.Emit(OpCodes.Ldstr, entryPoint);            // function name (EntryPoint or method name)
                il.Emit(OpCodes.Call, _nativeGetExport);       // → IntPtr

                if (binding.UsesFunctionPointer)
                {
                    // Function pointer path: store the native IntPtr directly into the function pointer field
                    il.Emit(OpCodes.Stsfld, binding.BackingField);
                }
                else
                {
                    // Delegate path: Marshal.GetDelegateForFunctionPointer<T>(IntPtr) → delegate instance
                    var closedGetDfp = new GenericInstanceMethod(_marshalGetDfpOpen);
                    closedGetDfp.GenericArguments.Add(binding.DelegateType!);
                    il.Emit(OpCodes.Call, ModuleDefinition.ImportReference(closedGetDfp));
                    il.Emit(OpCodes.Stsfld, binding.BackingField);
                }
            }
        }

        il.Emit(OpCodes.Ret);
    }

    // ── Extern method body replacement ──────────────────────────────────────

    /// <summary>
    /// Replaces the extern method body with dynamic-dispatch IL.
    /// <list type="bullet">
    ///   <item>Function pointer path (only for delegate* unmanaged): <c>arg0..argN → ldsfld fp → calli</c>.</item>
    ///   <item>Delegate path (default): <c>ldsfld delegate → arg0..argN → callvirt Invoke</c>.</item>
    /// </list>
    /// </summary>
    private void ReplaceExternBody(ExternBinding binding)
    {
        var method = binding.Extern;

        // Clear all P/Invoke-related flags
        method.PInvokeInfo    = null;
        method.Attributes     = (MethodAttributes)((int)method.Attributes & ~0x2000); // clear PInvokeImpl bit
        method.IsPInvokeImpl  = false;
        method.IsRuntime      = false;
        method.IsInternalCall = false;
        method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;

        var body = method.Body = new MethodBody(method);
        var il   = body.GetILProcessor();

        if (binding.UsesFunctionPointer)
        {
            // Function pointer path: arguments first, then function pointer, then calli
            for (var i = 0; i < method.Parameters.Count; i++)
                il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Ldsfld, binding.BackingField); // function pointer on top of stack
            
            // Create CallSite from FunctionPointerType
            var callSite = new CallSite(binding.FuncPtrType!.ReturnType)
            {
                CallingConvention = binding.FuncPtrType.CallingConvention
            };
            foreach (var param in binding.FuncPtrType.Parameters)
            {
                callSite.Parameters.Add(new ParameterDefinition(param.ParameterType));
            }
            
            il.Emit(OpCodes.Calli, callSite);
        }
        else
        {
            // Delegate path: delegate instance (this) first, then arguments, then callvirt Invoke
            il.Emit(OpCodes.Ldsfld, binding.BackingField);
            for (var i = 0; i < method.Parameters.Count; i++)
                il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Callvirt, binding.DelegateType!.Methods.First(m => m.Name == "Invoke"));
        }

        il.Emit(OpCodes.Ret);
        WriteInfo($"  Transformed: {method.DeclaringType.Name}::{method.Name}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SHARED HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether a method name passes the Include/Exclude filters.
    /// Include is applied before Exclude; if Include is non-empty the name must match at least one entry;
    /// if Exclude is non-empty the name must not match any entry.
    /// </summary>
    private static bool IsMethodIncluded(LibrarySpec spec, string methodName)
    {
        if (spec.IncludePatterns.Length > 0 &&
            !spec.IncludePatterns.Any(r => r.IsMatch(methodName)))
            return false;

        if (spec.ExcludePatterns.Length > 0 &&
            spec.ExcludePatterns.Any(r => r.IsMatch(methodName)))
            return false;

        return true;
    }

    /// <summary>
    /// Parses a semicolon-separated Glob pattern string into a pre-compiled regex array.
    /// Returns an empty array for null or whitespace input (no filtering effect).
    /// </summary>
    private static Regex[] ParsePatterns(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<Regex>();

        return value!
            .Split(';')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(GlobToRegex)
            .ToArray();
    }

    /// <summary>
    /// Converts a Glob pattern (<c>*</c> matches any character sequence, <c>?</c> matches any single character)
    /// into a case-insensitive full-string-match regular expression.
    /// </summary>
    private static Regex GlobToRegex(string glob)
    {
        var escaped = Regex.Escape(glob)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>Strips common native library extensions (.dll/.so/.dylib) to normalize comparisons.</summary>
    private static string StripExtension(string name)
    {
        foreach (var ext in new[] { ".dll", ".so", ".dylib" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ext.Length);
        return name;
    }

    /// <summary>Replaces non-alphanumeric characters with underscores to produce a valid C# identifier segment.</summary>
    private static string NormalizeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    /// <summary>Removes module references that are no longer referenced by any P/Invoke method after Static-mode replacement.</summary>
    private void CleanUnusedModuleReferences()
    {
        var usedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in AllTypes())
            foreach (var method in type.Methods)
                if (method.HasPInvokeInfo)
                    usedModules.Add(method.PInvokeInfo.Module.Name);

        var toRemove = ModuleDefinition.ModuleReferences
            .Where(r => !usedModules.Contains(r.Name))
            .ToList();

        foreach (var mr in toRemove)
        {
            ModuleDefinition.ModuleReferences.Remove(mr);
            WriteInfo($"  Removing unused module reference: \"{mr.Name}\"");
        }
    }

    /// <summary>Enumerates all types in the module (including nested types at any depth).</summary>
    private IEnumerable<TypeDefinition> AllTypes()
    {
        foreach (var t in ModuleDefinition.Types)
        {
            yield return t;
            foreach (var nested in NestedTypes(t))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> NestedTypes(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested))
                yield return deeper;
        }
    }
}
