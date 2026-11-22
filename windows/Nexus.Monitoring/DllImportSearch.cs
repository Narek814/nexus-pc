// P/Invoke in this assembly loads Windows DLLs from System32 only, never from the current folder or PATH (T6).
// NVML is not a P/Invoke at all: it is loaded by absolute path only (Nvml.cs).
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
