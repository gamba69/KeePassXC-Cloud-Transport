using KeePassXC.CloudTransport;

// This deliberately performs no network access and requires no credentials.
var remotePath = CloudPath.Combine("example", "config.json");
if (remotePath != "example/config.json") return 1;
Console.WriteLine("[PASS] CloudTransport NativeAOT smoke / public API linked");
return 0;
