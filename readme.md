# <img src="/package_icon.png" height="30px"> LazyImport.Fody

[![NuGet Status](https://img.shields.io/nuget/v/LazyImport.Fody.svg)](https://www.nuget.org/packages/LazyImport.Fody/)

Convert DllImport methods to runtime loading..

### This is an add-in for [Fody](https://github.com/Fody/Home/)

## Usage
 
### NuGet installation

Install the [LazyImport.Fody NuGet package](https://nuget.org/packages/LazyImport.Fody/) and update the [Fody NuGet package](https://nuget.org/packages/Fody/):

```powershell
PM> Install-Package Fody
PM> Install-Package LazyImport.Fody
```
 
### Add to FodyWeavers.xml

Add `<LazyImport/>` to [FodyWeavers.xml](https://github.com/Fody/Home/blob/master/pages/usage.md#add-fodyweaversxml).

```xml
<Weavers>
  <LazyImport />
</Weavers>
```

## Configuration

`LazyImport` supports two modes per `<Library>` item.

- **Dynamic mode** (default): rewrites matching `DllImport extern` methods to runtime symbol lookup and delegate/function-pointer dispatch.
- **Static mode**: keeps P/Invoke methods and only replaces the target library name in `DllImport` metadata.

### Basic dynamic configuration

```xml
<Weavers>
  <LazyImport>
    <Library Name="mylib" />
  </LazyImport>
</Weavers>
```

Equivalent explicit dynamic configuration:

```xml
<Weavers>
  <LazyImport>
    <Library Name="mylib" InitMethod="Initialize" />
  </LazyImport>
</Weavers>
```

### Dynamic mode with Include/Exclude filters

```xml
<Weavers>
  <LazyImport>
    <Library
      Name="mylib"
      InitMethod="Initialize"
      Include="test_basic_*;test_string_*"
      Exclude="test_basic_legacy_*" />
  </LazyImport>
</Weavers>
```

### Static mode (library name replacement)

```xml
<Weavers>
  <LazyImport>
    <Library Name="mylib" ReplaceName="__Internal" />
  </LazyImport>
</Weavers>
```

### Mixed mode (multiple libraries)

```xml
<Weavers>
  <LazyImport>
    <Library Name="mylib" InitMethod="InitializeMyLib" Include="api_*" />
    <Library Name="mylib2" ReplaceName="__Internal" />
  </LazyImport>
</Weavers>
```

## What gets injected in Dynamic mode

For each processed type/library pair, LazyImport injects:

- A library handle field: `__libraryHandle_{library}`
- One backing field per transformed method: `__fn_{library}_{method}`
- `Initialize(IntPtr libraryHandle)`
- `Initialize(string libraryPath)`

If a matching `Initialize(...)` stub already exists, its body is replaced.

## Notes and caveats

- `Name` is required on every `<Library>`.
- `InitMethod` and `ReplaceName` are mutually exclusive on the same `<Library>`.
- If both are omitted, mode defaults to Dynamic with `Initialize` as method name.
- Dynamic-mode `InitMethod` names must be unique across all configured libraries.
- `Include`/`Exclude` are semicolon-separated glob patterns (`*` = any sequence, `?` = single char).
- Include is evaluated first, then Exclude.
- If no `<Library>` entries are provided, LazyImport auto-detects target DLLs from P/Invoke methods.
  - Auto-detect works only when exactly one native library is found.
  - If multiple libraries are found, explicit `<Library>` configuration is required.
- Variadic (`VarArg`) methods are treated as Cdecl in dynamic binding.
- For signatures containing `delegate* unmanaged` (.NET 5+), function-pointer fields + `calli` are used instead of delegate invocation.
- In Dynamic mode, methods are no longer `PInvokeImpl`; in Static mode, they remain `PInvokeImpl`.
- Dynamic loader behavior:
  - Uses existing `NativeLibraryLoader` in the target assembly when available.
  - Otherwise injects `__FodyDynamicLoader` (`NativeLibrary` on .NET 5+, fallback P/Invoke loader for legacy targets).

```csharp
    class NativeLibraryLoader
    {
        public static IntPtr Load(string path)
        {
            // TODO: get dll handle
        }

        public static IntPtr GetExport(IntPtr handle, string symbol)
        {
            // TODO: get method handle by dll handle
        }
    }
```
