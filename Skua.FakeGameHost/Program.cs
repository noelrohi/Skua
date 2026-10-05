// The fake Game Host replays the scenario file named by SKUA_FAKE_GAMEHOST_SCENARIO, or else by its last argument (where
// skua-gamehost takes the SWF), one directive per line:
//
//   pidfile <path>        write this process's pid to <path>
//   calllog <path>        append the name of every C call to <path>, one per line (killLag with its argument)
//   send <type> <text>    send one frame of <type> (one character) with <text> as its UTF-8 payload
//   log <level> <text>    send an L frame: the level byte (1 error, 2 warn), then <text>
//   stderr <text>         write <text> as a line to stderr
//   repeat <n> <directive>  run <directive> n times, with {i} in it replaced by 0 to n-1
//   corrupt               send a frame header declaring a length of 0
//   reply <name> <xml>    answer every C call to <name> with <xml> (unscripted calls get <undefined/>)
//   delay <name> <ms>     answer C calls to <name> after <ms>, so later calls are answered first;
//                         the name `screenshot` delays S requests the same way; through `control` it changes the delay mid-run
//   noimage               answer S requests with no image (w = h = 0), as when the Game Host can't capture
//   stats <json>          answer Q requests with <json> ({} unless set); {n} in it is replaced by the number of Q requests so far
//   sleep <ms>            pause
//   note <text>           append <text> to the call log, from the thread running the directive
//   exit <code>           exit at once
//   control <path>        also run each line appended to <path> while the fake runs, so a test can act mid-run; each one run
//                         appends a line to <path>.done
//
// With `game <username> <password>` it also simulates the AQW game behind skua.swf (see FakeGame.cs), which accepts that
// account; `servers <json>`, `connect-delay <ms>`, `inventory-delay <ms>` (500 unless set; transfers are refused until then), `reject <server> <message>` and `account <username> <password>` (another account it accepts) configure it, and `lose-connection <message>`,
// `kick`, `idle-logout`, `logout-button`, `die`, `respawn-request` (the game's own resPlayerTimed; the game server ignores one within 2 s of the death,
// and respawns the player otherwise, as it does for the Engine's), `combat`, `afk`, `join <map>`, `cell <cell>`, `gain <xp> <gold>` (a level up sends `levelUp`), `blip` (the connection reads as dropped until the Engine's game state tracker has read it, i.e. asked isKicked; the call log records `blip read <n>`, from 1), `connection-message <message>`, `broken-login`,
// `login-response` (the last login's response again), `lock-map <map>` (transfers to it are ignored), `drop <id> <qty> <name>`,
// `pickup <id>`, `own <id> <category> <name>` (another item in the inventory, unequipped; the game equips an item in place of its
// category's), `equip-delay <ms|never>` (how long the game server takes to equip one from now on, 0 at first, or never), `focus <input|dynamic|none>` (the text field the stage's focus is on, e.g. chat's input), `packet <text>` (the game's
// packet call, as for a packet it sends) and `server-packet <packet>` (a string packet from the game server, e.g. `%xt%chatm%-1%zone~hi%Bob%`,
// handed to the game as SmartFox does) act in it.
// The call log adds ` lag-killed` to a screenshot taken while the game's lag killer hides the world, and records what the game did:
// `tfer <map> <cell> <pad>` for each map transfer, `jump <cell> <pad>`, `getBank` and `loadBank` (which the game server ignores),
// `respawn` or `respawn ignored` for each resPlayerTimed, `toggleBank open` or `toggleBank closed` for the bank panel, `loadShop <id>`, `showQuests <ids>`, `equipItem <id>`, `send <packet>` and
// `sendJson <packet>` for each packet sent to the server, `clientPacket <type> <packet>` for each handed to the game as the server's, and
// `connectTo <ip> <port>` (or with ` failed` or ` refused`) for the game's connectTo. connectTo connects over TCP to a game server on a
// loopback address, as the Packet Interceptor has it do, and refuses any other address; see FakeGame.ConnectTo.
//
// Like skua-gamehost, it answers C calls with R, P pings with P and Q stats with Q, and exits when its stdin closes.
// It answers S screenshots with I: a solid PNG of the 958x550 stage, scaled down to max_width like the real one, and a frame
// number that rises with each capture. The call log records each S as `screenshot <max_width>`.
//
// With `--frame-buffer=<name>` (the Mac App's Engine passes it) it maps that Frame Buffer before reading stdin, as skua-gamehost does,
// and speaks the Game View's frames:
//   W   the call log records `view live` or `view headless`, and a live view's viewport as `view viewport <w>x<h> <scale>`; while live it
//       writes a synthetic frame of the viewport's size (958x550 without one) every 33 ms, a solid colour whose red, green and blue bytes
//       are the frame number's low, middle and high bytes
//   U   the call log records each input event as `input <kind> <fields>`, e.g. `input mouseDown 479 275 left`, `input keyDown KeyA a` or
//       `input clipboard <text>`
//   O   with `button <x> <y> <w> <h>` (a rectangle on the 958x550 stage), a mouse move into it sends the hand cursor and one out of it the
//       arrow, as Ruffle does over a button; positions map from the viewport to the stage
//   K   with `selection <text>`, a Copy or Cut text control sends <text> as the game's clipboard
//   Q   its stats gain "live", "framesWritten", "inputEvents", "viewportWidth" and "viewportHeight"
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

Stream stdin = Console.OpenStandardInput();
Stream stdout = Console.OpenStandardOutput();
object writeLock = new();
Dictionary<string, string> replies = new();
ConcurrentDictionary<string, int> delays = new();
string? callLog = null;
object callLogLock = new();
bool noImage = false;
string stats = "{}";
int statsRequests = 0;
long frames = 0;
string? controlFile = null;
FakeGame? game = null;
FakeFrameBuffer? frameBuffer = args.FirstOrDefault(a => a.StartsWith("--frame-buffer=", StringComparison.Ordinal)) is { } fbArg
    ? FakeFrameBuffer.Open(fbArg["--frame-buffer=".Length..])
    : null;
long inputEvents = 0;
// The viewport a live view renders at, the simulated button on the stage, whether the pointer is over it, and the text a Copy copies.
(int Width, int Height) viewport = (958, 550);
(float X, float Y, float W, float H)? button = null;
bool overButton = false;
string? selection = null;

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

// The stdin loop and the simulated game's own threads (connectTo's connection) both record here. .NET appends at the end it last saw, not
// with O_APPEND, so two appends at once can write at the same offset and one line overwrites the other.
void Record(IEnumerable<string> lines)
{
    if (callLog is null)
        return;
    lock (callLogLock)
        File.AppendAllLines(callLog, lines);
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
        case ["stats", ..]:
            stats = line["stats ".Length..];
            break;
        case ["control", string file]:
            controlFile = file;
            break;
        case ["game", string username, string password]:
            game = new FakeGame(username, password, invoke => Send('E', Encoding.UTF8.GetBytes(invoke)), note => Record([note]));
            break;
        case ["servers", ..]:
            game?.Servers(line["servers ".Length..]);
            break;
        case ["connect-delay", string ms]:
            game?.ConnectDelay(int.Parse(ms));
            break;
        case ["inventory-delay", string ms]:
            game?.InventoryDelay(int.Parse(ms));
            break;
        case ["reject", string server, string message]:
            game?.Reject(server, message);
            break;
        case ["account", string username, string password]:
            game?.Account(username, password);
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
                Record([name == "killLag" ? $"killLag {Regex.Match(invoke, "<(true|false)/>").Groups[1].Value}" : name]);
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
                string json = stats.Replace("{n}", (++statsRequests).ToString());
                if (frameBuffer is not null)
                {
                    string fields = $"\"live\":{(frameBuffer.Live ? "true" : "false")},\"framesWritten\":{frameBuffer.Written},\"inputEvents\":{Interlocked.Read(ref inputEvents)},\"viewportWidth\":{viewport.Width},\"viewportHeight\":{viewport.Height}";
                    json = json.Trim() == "{}" ? $"{{{fields}}}" : json.TrimEnd()[..^1] + "," + fields + "}";
                }
                SendReply('Q', id, json);
                break;
            case 'W' when body.Length >= 6:
                bool live = body[5] != 0;
                List<string> lines = [live ? "view live" : "view headless"];
                viewport = (958, 550);
                if (live && body.Length >= 18)
                {
                    // Clamped to the slots, as skua-gamehost does.
                    (int slotWidth, int slotHeight) = frameBuffer?.Max ?? (958, 550);
                    viewport = (Math.Clamp((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)), 958, slotWidth),
                        Math.Clamp((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)), 550, slotHeight));
                    lines.Add($"view viewport {viewport.Width}x{viewport.Height} {BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(14)).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                Record(lines);
                frameBuffer?.SetLive(live, viewport.Width, viewport.Height);
                break;
            case 'U':
                Interlocked.Increment(ref inputEvents);
                Record(["input " + FakeInput.Describe(body.AsSpan(5))]);
                if (FakeInput.Position(body.AsSpan(5)) is { } at && button is { } b)
                {
                    // Viewport pixels to the stage, as Ruffle maps them.
                    float x = at.X * 958 / viewport.Width, y = at.Y * 550 / viewport.Height;
                    bool over = x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H;
                    if (over != overButton)
                    {
                        overButton = over;
                        Send('O', [over ? (byte)1 : (byte)0, 1]);
                    }
                }
                if (FakeInput.TextControl(body.AsSpan(5)) is "Copy" or "Cut" && selection is not null)
                    Send('K', Encoding.UTF8.GetBytes(selection));
                break;
            case 'S':
                uint maxWidth = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5));
                Record([$"screenshot {maxWidth}{(game?.LagKilled == true ? " lag-killed" : "")}"]);
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
        case ["stderr", ..]:
            Console.Error.WriteLine(line["stderr ".Length..]);
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
        case ["button", ..]:
            float[] r = line["button ".Length..].Split(' ').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            button = (r[0], r[1], r[2], r[3]);
            break;
        case ["selection", ..]:
            selection = line["selection ".Length..];
            break;
        case ["sleep", string ms]:
            Thread.Sleep(int.Parse(ms));
            break;
        case ["note", ..]:
            Record([line["note ".Length..]]);
            break;
        case ["delay", string name, string ms]:
            delays[name] = int.Parse(ms);
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
