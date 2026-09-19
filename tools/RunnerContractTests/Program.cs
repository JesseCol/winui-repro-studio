using System.Text;
using ReproStudio.Shared;
using ReproStudio_Runner.Services;
using ReproStudio.Tests;

// No test framework or UI dependency. Run: dotnet run --project tools/RunnerContractTests
string folder = Path.Combine(Path.GetTempPath(), "runner-contracts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int passed = 0;
try
{
    const string remainder = "// repro: unchanged \u03bb\n// topmost: yes\n\nclass Repro {\n// wasdk: body comment\n}\n";
    string rewritten = RuntimeHeaderEdit.Rewrite("// WASDK: 1\n// winui: old\n// wasdk: 2\n" + remainder, "winui", "2.3.9");
    Assert(rewritten == "// winui: 2.3.9\n" + remainder, "all leading runtime headers removed, unrelated text unchanged");
    Assert(RuntimeHeaderEdit.Rewrite(rewritten, "winui", "2.3.9") == rewritten, "same published choice is text-idempotent");
    Assert(RuntimeHeaderEdit.Rewrite("// wasdk: old", "wasdk", "2.2.0") == "// wasdk: 2.2.0\n", "unterminated header handled");
    Throws<ArgumentException>(() => RuntimeHeaderEdit.Rewrite("", "both", "2.2.0"), "mixed mode rejected");
    Throws<ArgumentException>(() => RuntimeHeaderEdit.Rewrite("", "wasdk", "../other"), "non-version rejected");
    Throws<ArgumentException>(() => RuntimeHeaderEdit.Rewrite("", "winui", "2.3.9\n// injected: yes"), "header injection rejected");

    foreach (Encoding encoding in new Encoding[]
    {
        new UTF8Encoding(false, true), new UTF8Encoding(true, true),
        new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
    })
    {
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string path = Path.Combine(folder, "source.cs");
            string original = "// wasdk: 2.2.0" + newline + remainder.Replace("\n", newline);
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(original)];
            File.WriteAllBytes(path, bytes);
            RuntimeHeaderEdit edit = RuntimeHeaderEdit.Read(path);
            edit.Commit(path, "winui", "2.3.9");
            byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("// winui: 2.3.9" + newline + remainder.Replace("\n", newline))];
            Assert(File.ReadAllBytes(path).SequenceEqual(expected), $"{encoding.WebName} BOM={encoding.GetPreamble().Length} newline={newline.Length} exact bytes");
        }
    }

    string source = Path.Combine(folder, "concurrent.cs");
    File.WriteAllText(source, "// wasdk: 2.2.0\nclass Original {}");
    RuntimeHeaderEdit snapshot = RuntimeHeaderEdit.Read(source);
    File.WriteAllText(source, "// wasdk: 2.2.0\nclass EditorChange {}");
    Throws<IOException>(() => snapshot.Commit(source, "winui", "2.3.9"), "concurrent editor change rejected");
    Assert(File.ReadAllText(source).Contains("EditorChange"), "concurrent editor bytes survive");
    Throws<IOException>(() => RuntimeHeaderEdit.Read(source, "// wasdk: 2.2.0\nclass Original {}"),
        "a save awaiting reload is rejected against the preview source snapshot");
    Assert(RuntimeHeaderEdit.Read(source, "// wasdk: 2.2.0\nclass EditorChange {}").Text.Contains("EditorChange"),
        "the current preview source snapshot remains editable");

    string runtimeSource = Path.Combine(folder, "stale-runtime.cs");
    const string renderedSource = "// wasdk: 2.4.1-experimental\n// sdk: match\nclass Repro {}";
    string savedSource = renderedSource.Replace("// sdk: match", "// sdk: base");
    File.WriteAllText(runtimeSource, savedSource);
    Throws<IOException>(() => RuntimeHeaderEdit.Read(runtimeSource, renderedSource),
        "a just-saved SDK header cannot be overwritten by a stale Runtime dialog");
    Assert(File.ReadAllText(runtimeSource) == savedSource, "rejecting stale Runtime Apply leaves saved headers untouched");
    File.SetAttributes(source, FileAttributes.ReadOnly);
    Throws<UnauthorizedAccessException>(() => RuntimeHeaderEdit.Read(source), "read-only preflight rejected");
    File.SetAttributes(source, FileAttributes.Normal);
    File.WriteAllBytes(source, [0xff, 0xfd, 0xaa]);
    Throws<DecoderFallbackException>(() => RuntimeHeaderEdit.Read(source), "undecodable source rejected");
    Throws<FileNotFoundException>(() => RuntimeHeaderEdit.Read(source + ".missing"), "missing source rejected");

    string prefsPath = RunnerPreferences.GetPath(folder);
    Assert(!RunnerPreferences.Load(prefsPath).IsPinned, "missing preference defaults off");
    new RunnerPreferences { IsPinned = true }.Save(prefsPath);
    Assert(RunnerPreferences.Load(prefsPath).IsPinned, "pin survives a fresh read");
    await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        new RunnerPreferences { IsPinned = i % 2 == 0 }.Save(prefsPath))));
    _ = RunnerPreferences.Load(prefsPath);
    passed++;
    File.WriteAllText(prefsPath, "not json");
    Throws<System.Text.Json.JsonException>(() => RunnerPreferences.Load(prefsPath), "malformed preference visible");
    new RunnerPreferences { IsPinned = false }.Save(prefsPath);
    Assert(!RunnerPreferences.Load(prefsPath).IsPinned, "unpin persists");
    File.SetAttributes(prefsPath, FileAttributes.ReadOnly);
    Throws<UnauthorizedAccessException>(() => new RunnerPreferences { IsPinned = true }.Save(prefsPath),
        "unwritable preference is not silently accepted");
    File.SetAttributes(prefsPath, FileAttributes.Normal);
    Assert(!RunnerPreferences.Load(prefsPath).IsPinned, "failed preference write preserves saved value");

    string special = Path.Combine(folder, "space # % \u03bb & $(nope).cs");
    var start = CodeEditor.CreateStartInfo(@"C:\Program Files\Microsoft VS Code\Code.exe", special);
    Assert(!start.UseShellExecute && start.ArgumentList.SequenceEqual(new[] { "--reuse-window", "--", special }),
        "Code receives original path as one literal argument, without a shell");
    Throws<FileNotFoundException>(() => CodeEditor.Open(special), "missing Code source fails before launch");

    string package = Path.Combine(folder, "Microsoft.WindowsAppSDK.WinUI.3.0.0-dev.nupkg");
    File.WriteAllText(package, "one");
    string firstKey = WinUiOverride.ForLocalPackage(package).CacheKey;
    File.WriteAllText(package, "two");
    Assert(firstKey != WinUiOverride.ForLocalPackage(package).CacheKey, "local package content key unchanged, not filename key");
    string localHeaders = RuntimeHeaderEdit.Rewrite("// wasdk: 2.2.0\n// winui: 2.3.9\nclass Repro {}", "winui", package);
    Assert(localHeaders == "// winui: " + package + "\nclass Repro {}", "local path replaces both runtime header keys");

    string renderPath = Path.Combine(folder, "request.json");
    var snippet = new Snippet { ControlHost = new RunnerControlHost(), RequestId = Guid.NewGuid(), SourcePath = special };
    File.WriteAllText(renderPath, "{\"topmost\":true,\"unrecognizedFutureField\":42}");
    Assert(SnippetIo.TryRead(renderPath) is not null, "legacy topmost and unknown JSON fields ignored");
    var client = new RunnerControlClient(renderPath);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Task<RunnerControlResponse> pending = client.SendAsync(snippet, "list", "wasdk", false, null, deadline.Token);
    RunnerControlRequest command;
    while (RunnerControlIo.ReadRequest(renderPath) is not { } observed) await Task.Delay(10, deadline.Token);
    command = RunnerControlIo.ReadRequest(renderPath)!;
    RunnerControlIo.WriteResponse(renderPath, new RunnerControlResponse
    {
        Id = Guid.NewGuid(), SessionId = snippet.ControlHost.SessionId, Versions = ["stale"],
    });
    await Task.Delay(250, deadline.Token);
    Assert(!pending.IsCompleted, "stale command response rejected");
    RunnerControlIo.WriteResponse(renderPath, new RunnerControlResponse
    {
        Id = command.Id, SessionId = Guid.NewGuid(), Versions = ["wrong session"],
    });
    await Task.Delay(250, deadline.Token);
    Assert(!pending.IsCompleted, "wrong-session response rejected");
    RunnerControlIo.WriteResponse(renderPath, new RunnerControlResponse
    {
        Id = command.Id, SessionId = snippet.ControlHost.SessionId, Versions = ["2.2.0"],
    });
    Assert((await pending).Versions.SequenceEqual(new[] { "2.2.0" }), "correlated response accepted");
    Assert(!File.Exists(RunnerControlIo.RequestPath(renderPath)), "completed command withdrawn");
    using var cancelled = new CancellationTokenSource();
    Task<RunnerControlResponse> cancelledRequest = client.SendAsync(snippet, "list", "winui", true, null, cancelled.Token);
    while (RunnerControlIo.ReadRequest(renderPath) is null) await Task.Delay(10, deadline.Token);
    cancelled.Cancel();
    try { await cancelledRequest; throw new Exception("Cancellation not observed."); }
    catch (OperationCanceledException) { passed++; }
    Assert(!File.Exists(RunnerControlIo.RequestPath(renderPath)), "cancelled command withdrawn");
    snippet.ControlHost = null;
    try
    {
        await client.SendAsync(snippet, "list", "wasdk", false, null, deadline.Token);
        throw new Exception("No-watch command unexpectedly succeeded.");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message.Contains("--no-watch"), "one-shot client explains how to enable runtime changes");
    }
    snippet.ControlHost = new RunnerControlHost { ProcessId = int.MaxValue };
    try
    {
        await client.SendAsync(snippet, "list", "wasdk", false, null, deadline.Token);
        throw new Exception("Exited-host command unexpectedly succeeded.");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message.Contains("host has exited"), "exited host produces actionable client error");
    }
    await SdkContractChecks.RunAsync(folder, Assert);
    Console.WriteLine($"PASS: {passed} Runner contract checks.");
    return 0;
}
finally
{
    Directory.Delete(folder, recursive: true);
}

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Assert(true, name); return; }
    throw new Exception("FAIL: " + name + " (no expected exception)");
}
