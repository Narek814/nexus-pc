// CA5392 (Directory.Build.props) also covers the test helpers' P/Invoke (RestrictedToken, DllSearchProbe): System32 only.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
