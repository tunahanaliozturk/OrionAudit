; Unshipped analyzer release.
; Add analyzer rules here as they are developed for the next release.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OA0001  | Usage    | Warning  | An [OrionAuditModule] type, or a type it is nested in, is not partial, so no registration code is generated for it.
OA0002  | Usage    | Warning  | An [Auditable] type is abstract, so the generated module does not register it.
OA0003  | Usage    | Warning  | An [Auditable] type is not reachable from the generated module, so it is not registered.
