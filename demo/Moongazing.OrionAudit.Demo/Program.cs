using Moongazing.OrionAudit.Demo;

Console.WriteLine();
Console.WriteLine("###############################################################");
Console.WriteLine("#  OrionAudit - runnable demo (pure, in-memory, no database)  #");
Console.WriteLine("#  EF Core change-audit trail: snapshots, RFC 6902 diffs,     #");
Console.WriteLine("#  time-travel reconstruction, and rendered audit views.      #");
Console.WriteLine("###############################################################");

// 1. Snapshotting with field rules (hash / redact / exclude).
SnapshotDemo.Run();

// 2. RFC 6902 JSON Patch diff between two states, plus a replay round-trip.
DiffDemo.Run();

// 3. Time-travel reconstruction over an in-memory lifecycle trail.
var (trail, _) = TimeTravelDemo.Run();

// 4. Render the same trail into a human-readable change history.
AuditViewDemo.Run(trail);

// 5. Step-by-step replay: the reconstructed state after each event.
ReplayStepDemo.Run(trail);

Console.WriteLine();
Console.WriteLine(new string('=', 72));
Console.WriteLine("  All demos completed successfully.");
Console.WriteLine(new string('=', 72));
Console.WriteLine();
