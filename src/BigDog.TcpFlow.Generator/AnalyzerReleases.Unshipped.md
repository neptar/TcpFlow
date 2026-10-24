; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TCPFLOW001 | TcpFlow | Error | Type needs a parameterless constructor
TCPFLOW002 | TcpFlow | Error | Type is not accessible to generated code
TCPFLOW003 | TcpFlow | Error | Generic types are not supported
TCPFLOW004 | TcpFlow | Error | Flow type id collision
TCPFLOW005 | TcpFlow | Error | Flow type id 0 is reserved
TCPFLOW006 | TcpFlow | Error | Member type cannot be transmitted
TCPFLOW007 | TcpFlow | Warning | Member will not be transmitted
TCPFLOW008 | TcpFlow | Warning | Flow types should contain only data
TCPFLOW009 | TcpFlow | Error | Flow types must not be [MemoryPackable]
TCPFLOW010 | TcpFlow | Warning | Transmitted members are split across partial declarations
TCPFLOW011 | TcpFlow | Error | Too many transmitted members
