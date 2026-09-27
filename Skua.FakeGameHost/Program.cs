// The fake Game Host replays the scenario file named by SKUA_FAKE_GAMEHOST_SCENARIO, or else by its last argument (where
// skua-gamehost takes the SWF), one directive per line:
//
//   pidfile <path>        write this process's pid to <path>
//   calllog <path>        append the name of every C call to <path>, one per line (killLag with its argument)
//   send <type> <text>    send one frame of <type> (one character) with <text> as its UTF-8 payload
//   log <level> <text>    send an L frame: the level byte (1 error, 2 warn), then <text>
//   repeat <n> <directive>  run <directive> n times, with {i} in it replaced by 0 to n-1
//   corrupt               send a frame header declaring a length of 0
//   reply <name> <xml>    answer every C call to <name> with <xml> (unscripted calls get <undefined/>)
//   delay <name> <ms>     answer C calls to <name> after <ms>, so later calls are answered first;
//                         the name `screenshot` delays S requests the same way
//   noimage               answer S requests with no image (w = h = 0), as when the Game Host can't capture
//   sleep <ms>            pause
//   exit <code>           exit at once
//   control <path>        also run each line appended to <path> while the fake runs, so a test can act mid-run; each one run
//                         appends a line to <path>.done
//
// With `game <username> <password>` it also simulates the AQW game behind skua.swf (see FakeGame.cs), which accepts that
// account; `servers <json>`, `connect-delay <ms>` and `reject <server> <message>` configure it, and `lose-connection <message>`,
// `kick`, `logout-button`, `die`, `afk`, `join <map>`, `cell <cell>`, `blip <ms>`, `connection-message <message>` and `broken-login` act in it.
// The call log adds ` lag-killed` to a screenshot taken while the game's lag killer hides the world.
//
// Like skua-gamehost, it answers C calls with R, P pings with P and Q stats with Q, and exits when its stdin closes.
// It answers S screenshots with I: a solid PNG of the 958x550 stage, scaled down to max_width like the real one, and a frame
// number that rises with each capture. The call log records each S as `screenshot <max_width>`.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

Stream stdin = Console.OpenStandardInput();
Stream stdout = Console.OpenStandardOutput();
object writeLock = new();
Dictionary<string, string> replies = new();
Dictionary<string, int> delays = new();
string? callLog = null;
bool noImage = false;
long frames = 0;
string? controlFile = null;
FakeGame? game = null;

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
        case ["noimage"]:
            noImage = true;
            break;
        case ["control", string file]:
            controlFile = file;
            break;
        case ["game", string username, string password]:
            game = new FakeGame(username, password, invoke => Send('E', Encoding.UTF8.GetBytes(invoke)));
            break;
        case ["servers", ..]:
            game?.Servers(line["servers ".Length..]);
            break;
        case ["connect-delay", string ms]:
            game?.ConnectDelay(int.Parse(ms));
            break;
        case ["reject", string server, string message]:
            game?.Reject(server, message);
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
                string invoke = Encoding.UTF8.GetString(body, 5, body.Length - 5);
                string name = Regex.Match(invoke, "name=\"([^\"]*)\"").Groups[1].Value;
                if (callLog is not null)
                    File.AppendAllLines(callLog, [name == "killLag" ? $"killLag {Regex.Match(invoke, "<(true|false)/>").Groups[1].Value}" : name]);
                string reply = game?.Answer(invoke) ?? replies.GetValueOrDefault(name, "<undefined/>");
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
            case 'S':
                uint maxWidth = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5));
                if (callLog is not null)
                    File.AppendAllLines(callLog, [$"screenshot {maxWidth}{(game?.LagKilled == true ? " lag-killed" : "")}"]);
                byte[] image = Screenshot(id, maxWidth);
                if (delays.TryGetValue("screenshot", out int screenshotDelay))
                    Task.Delay(screenshotDelay).ContinueWith(_ => Send('I', image));
                else
                    Send('I', image);
                break;
        }
    }
    Environment.Exit(0);
});
reader.Start();

if (controlFile is not null)
{
    new Thread(() =>
    {
        long read = 0;
        while (true)
        {
            if (File.Exists(controlFile))
            {
                using FileStream stream = new(controlFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Position = read;
                string text = new StreamReader(stream).ReadToEnd();
                int end = text.LastIndexOf('\n') + 1;
                read += Encoding.UTF8.GetByteCount(text[..end]);
                foreach (string line in text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Run(line);
                    File.AppendAllText(controlFile + ".done", "\n");
                }
            }
            Thread.Sleep(20);
        }
    }) { IsBackground = true }.Start();
}

foreach (string line in scenario)
    Run(line);

reader.Join();

void Run(string line)
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
        case ["log", string level, string text]:
            Send('L', [byte.Parse(level), .. Encoding.UTF8.GetBytes(text)]);
            break;
        case ["repeat", string count, string directive]:
            for (int i = 0; i < int.Parse(count); i++)
                Run(directive.Replace("{i}", i.ToString()));
            break;
        case ["corrupt"]:
            lock (writeLock)
            {
                stdout.Write(new byte[4]);
                stdout.Flush();
            }
            break;
        case ["sleep", string ms]:
            Thread.Sleep(int.Parse(ms));
            break;
        case ["exit", string code]:
            Environment.Exit(int.Parse(code));
            break;
        default:
            game?.Run(line);
            break;
    }
}

// The 'I' payload: id, w, h, frame, then the PNG.
byte[] Screenshot(uint id, uint maxWidth)
{
    const int stageWidth = 958, stageHeight = 550;
    long frame = Interlocked.Increment(ref frames);
    (uint w, uint h) = noImage ? (0u, 0u)
        : maxWidth > 0 && maxWidth < stageWidth ? (maxWidth, (uint)Math.Max(1, Math.Round(stageHeight * (double)maxWidth / stageWidth)))
        : (stageWidth, stageHeight);
    byte[] png = noImage ? [] : Png((int)w, (int)h);
    byte[] payload = new byte[20 + png.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, id);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), w);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), h);
    BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(12), (ulong)frame);
    png.CopyTo(payload, 20);
    return payload;
}

// A solid-colour 8-bit RGB PNG.
static byte[] Png(int width, int height)
{
    MemoryStream pixels = new();
    using (ZLibStream zlib = new(pixels, CompressionLevel.Fastest, leaveOpen: true))
    {
        byte[] row = new byte[1 + (width * 3)];
        for (int x = 0; x < width; x++)
            (row[1 + (x * 3)], row[2 + (x * 3)], row[3 + (x * 3)]) = ((byte)0x20, (byte)0x40, (byte)0x80);
        for (int y = 0; y < height; y++)
            zlib.Write(row);
    }

    byte[] header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header, width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8; // bit depth
    header[9] = 2; // RGB
    MemoryStream png = new();
    png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
    Chunk(png, "IHDR", header);
    Chunk(png, "IDAT", pixels.ToArray());
    Chunk(png, "IEND", []);
    return png.ToArray();
}

static void Chunk(Stream png, string type, byte[] data)
{
    byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
    Span<byte> number = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
    png.Write(number);
    png.Write(typed);
    BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
    png.Write(number);
}

static uint Crc32(byte[] bytes)
{
    uint crc = 0xFFFFFFFF;
    foreach (byte b in bytes)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
    }
    return ~crc;
}
