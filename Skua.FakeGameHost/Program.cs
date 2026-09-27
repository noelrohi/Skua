// The fake Game Host replays the scenario file named by SKUA_FAKE_GAMEHOST_SCENARIO, or else by its last argument (where
// skua-gamehost takes the SWF), one directive per line:
//
//   pidfile <path>        write this process's pid to <path>
//   calllog <path>        append the name of every C call to <path>, one per line
//   send <type> <text>    send one frame of <type> (one character) with <text> as its UTF-8 payload
//   reply <name> <xml>    answer every C call to <name> with <xml> (unscripted calls get <undefined/>)
//   delay <name> <ms>     answer C calls to <name> after <ms>, so later calls are answered first
//   sleep <ms>            pause
//   exit <code>           exit at once
//
// Like skua-gamehost, it answers C calls with R, P pings with P and Q stats with Q, and exits when its stdin closes.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

Stream stdin = Console.OpenStandardInput();
Stream stdout = Console.OpenStandardOutput();
object writeLock = new();
Dictionary<string, string> replies = new();
Dictionary<string, int> delays = new();
string? callLog = null;

void Send(char type, ReadOnlySpan<byte> payload)
{
    byte[] frame = new byte[5 + payload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(1 + payload.Length));
    frame[4] = (byte)type;
    payload.CopyTo(frame.AsSpan(5));
    lock (writeLock)
    {
        stdout.Write(frame);
        stdout.Flush();
    }
}

void SendReply(char type, uint id, string text)
{
    byte[] payload = new byte[4 + Encoding.UTF8.GetByteCount(text)];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, id);
    Encoding.UTF8.GetBytes(text, payload.AsSpan(4));
    Send(type, payload);
}

string? scenarioPath = Environment.GetEnvironmentVariable("SKUA_FAKE_GAMEHOST_SCENARIO") is { Length: > 0 } path ? path : args.LastOrDefault();
string[] scenario = scenarioPath is not null && File.Exists(scenarioPath) ? File.ReadAllLines(scenarioPath) : [];
foreach (string line in scenario)
{
    switch (line.Split(' ', 3))
    {
        case ["reply", string name, string xml]:
            replies[name] = xml;
            break;
        case ["delay", string name, string ms]:
            delays[name] = int.Parse(ms);
            break;
        case ["calllog", string file]:
            callLog = file;
            break;
    }
}

Thread reader = new(() =>
{
    byte[] header = new byte[4];
    while (stdin.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4)
    {
        byte[] body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
        if (stdin.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length)
            break;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1));
        switch ((char)body[0])
        {
            case 'C':
                string name = Regex.Match(Encoding.UTF8.GetString(body, 5, body.Length - 5), "name=\"([^\"]*)\"").Groups[1].Value;
                if (callLog is not null)
                    File.AppendAllLines(callLog, [name]);
                string reply = replies.GetValueOrDefault(name, "<undefined/>");
                if (delays.TryGetValue(name, out int delay))
                    Task.Delay(delay).ContinueWith(_ => SendReply('R', id, reply));
                else
                    SendReply('R', id, reply);
                break;
            case 'P':
                SendReply('P', id, "");
                break;
            case 'Q':
                SendReply('Q', id, "{}");
                break;
        }
    }
    Environment.Exit(0);
});
reader.Start();

foreach (string line in scenario)
{
    string[] parts = line.Split(' ', 3);
    switch (parts)
    {
        case ["pidfile", string pidFile]:
            File.WriteAllText(pidFile, Environment.ProcessId.ToString());
            break;
        case ["send", string type, string text]:
            Send(type[0], Encoding.UTF8.GetBytes(text));
            break;
        case ["sleep", string ms]:
            Thread.Sleep(int.Parse(ms));
            break;
        case ["exit", string code]:
            Environment.Exit(int.Parse(code));
            break;
    }
}

reader.Join();
