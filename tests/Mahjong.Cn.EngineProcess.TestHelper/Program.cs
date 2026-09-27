using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Test-only synthetic protocol peer. Never referenced or packaged by the plugin.
Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false, true);
string mode = args.FirstOrDefault() ?? "echo";
int requestsRead = 0;
if (mode == "sleep-child") { await Task.Delay(Timeout.Infinite); return; }
if (mode == "unsolicited")
{
    Console.WriteLine("{\"unsolicited\":true}");
    await Task.Delay(Timeout.Infinite);
    return;
}
while (await Console.In.ReadLineAsync() is { } request)
{
    requestsRead++;
    switch (mode)
    {
        case "hang":
            await Task.Delay(Timeout.Infinite);
            break;
        case "delay":
            await Task.Delay(int.Parse(args[1]));
            Console.WriteLine(request);
            break;
        case "stderr":
            await Console.Error.WriteAsync("TEST_PRIVATE_STDERR_ONLY:" + new string('x', 100_000));
            await Console.Error.FlushAsync();
            Console.WriteLine(request);
            break;
        case "extra":
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(request + "\n" + request + "\n"));
            break;
        case "late-extra":
            Console.WriteLine(request);
            await Task.Delay(200);
            Console.WriteLine(request);
            break;
        case "invalid-json": Console.WriteLine("synthetic non-json output"); break;
        case "array": Console.WriteLine("[]"); break;
        case "batch":
            if (requestsRead == 3) Console.WriteLine("[" + request + "]");
            break;
        case "long-bytes": Console.WriteLine("{\"text\":\"" + new string('中', 200) + "\"}"); break;
        case "long-chars": Console.WriteLine(JsonSerializer.Serialize(new { text = new string('x', 200) })); break;
        case "oversized-no-newline":
            Console.Write(new string('x', 97));
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.Infinite);
            break;
        case "invalid-utf8":
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xFF, (byte)'\n' });
            break;
        case "eof": return;
        case "crash": Environment.Exit(7); break;
        case "partial-eof": Console.Write("{\"partial\":"); return;
        case "arguments": Console.WriteLine(JsonSerializer.Serialize(new { values = args.Skip(1).ToArray() })); break;
        case "environment":
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                threads = Environment.GetEnvironmentVariable("OMP_NUM_THREADS"),
                dynamic = Environment.GetEnvironmentVariable("OMP_DYNAMIC"),
            }));
            break;
        case "descendant":
        {
            var info = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true,
            };
            info.ArgumentList.Add("--roll-forward"); info.ArgumentList.Add("Major");
            info.ArgumentList.Add(typeof(Program).Assembly.Location); info.ArgumentList.Add("sleep-child");
            using var child = Process.Start(info)!;
            Console.WriteLine(JsonSerializer.Serialize(new { childPid = child.Id }));
            await Task.Delay(Timeout.Infinite);
            break;
        }
        default: Console.WriteLine(request); break;
    }
    await Console.Out.FlushAsync();
}
