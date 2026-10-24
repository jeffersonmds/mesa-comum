using Microsoft.AspNetCore.SignalR;

if (args.Contains("--selftest")) { Rules.SelfTest(); return; }

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/hub");

// Turn timers and bots run on a clock, not on player actions
var hub = app.Services.GetRequiredService<IHubContext<GameHub>>();
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    while (await timer.WaitForNextTickAsync())
        try { await GameHub.Tick(hub.Clients); }
        catch (Exception e) { Console.Error.WriteLine(e); }
});

app.Run();

enum Wild { None, Plain, Mirror, Double, Chameleon }

record SetCheck(bool Valid, int Owner, string? Reason);

record Mode(string Name, int HandSize, int TurnSeconds, int WildSets);

record Move(List<List<Tile>> Table, List<Tile> Hand); // a planned table + what's left in hand

class Tile
{
    public int Id { get; init; }
    public int Num { get; init; } // 0 for wild tiles
    public int Color { get; init; } // -1 for wild tiles
    public Wild Wild { get; init; }
    public int Placer { get; set; } = -1; // seat that put it on the table; -1 while in a hand
}

class Player
{
    public required string Token { get; init; } // secret per browser tab, used to reclaim the seat after a refresh
    public required string Name { get; set; } // bots are renamed when seats shift
    public int Seat { get; set; } // index in Room.Players

    public bool IsBot { get; init; }
    public int Level { get; init; } = 2; // bots only: 1 easy, 2 medium, 3 hard
    public int Steals { get; set; } // this match, for the end-of-game highlights
    public string? ConnId { get; set; }
    public List<Tile> Hand { get; set; } = [];
}

class Room
{
    public required string Code { get; init; }
    public List<Player> Players { get; } = [];
    public List<Tile> Bag { get; set; } = [];
    public List<List<Tile>> Table { get; set; } = [];
    public Mode Mode { get; set; } = Rules.Modes["normal"];
    public int Turn { get; set; }
    public DateTime TurnStarted { get; set; }
    public bool Started { get; set; }
    public bool Over { get; set; }
    public int Finisher { get; set; } = -1; // seat that emptied their hand
    public int Passes { get; set; } // consecutive passes with an empty bag
    public HashSet<int> Fresh { get; set; } = []; // tiles laid in the last play, highlighted for everyone
    public string Log { get; set; } = "";
    public string Kind { get; set; } = ""; // last event: start | play | steal | draw | end
    public int Seq { get; set; } // bumps on every event, so each client plays its sound once
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public string Theme { get; set; } = "feltro";
    public (int Pts, string? Text) BestSteal { get; set; } // highlights shown on the podium
    public (int Tiles, string? Text) BestPlay { get; set; }
}

static class Rules
{
    public const int Colors = 5, MaxNum = 10, WildValue = 5;

    public static readonly Dictionary<string, Mode> Modes = new()
    {
        ["normal"] = new("Normal", 12, 90, 1),
        ["rapida"] = new("Rápida", 8, 45, 1),
        ["caos"] = new("Caos", 12, 90, 3),
    };

    public static readonly string[] Themes = ["feltro", "taverna", "praia", "espaco"]; // folders under wwwroot/img
    public static readonly string[] Animals = ["Raposa", "Coruja", "Sapo", "Gato"]; // same order as img/<theme>/avatar{seat}

    public static string? RemoveBot(Room r, int seat)
    {
        if (r.Started && !r.Over) return "Dá pra tirar bot só entre partidas.";
        if (seat < 0 || seat >= r.Players.Count || !r.Players[seat].IsBot) return "Esse jogador não é um bot.";
        FreeSeat(r, seat);
        return null;
    }

    // A person leaving. Mid-game they keep their seat (offline, so their turns auto-draw) and can come back with
    // the room code; otherwise the seat is freed, and if the owner left, whoever moves into seat 0 owns the room.
    public static void Leave(Room r, Player p)
    {
        p.ConnId = null;
        if (!r.Started || r.Over) FreeSeat(r, p.Seat);
    }

    // Seats are list positions, so later players move up and bots take the name of their new seat's animal.
    // After a finished game this goes back to the lobby, since the old table's owners point at the old seats.
    static void FreeSeat(Room r, int seat)
    {
        r.Players.RemoveAt(seat);
        for (var i = 0; i < r.Players.Count; i++)
        {
            r.Players[i].Seat = i;
            if (r.Players[i].IsBot) r.Players[i].Name = $"Bot {Animals[i]}";
        }
        r.Started = r.Over = false;
        r.Table = [];
        r.Fresh = [];
        r.Log = "";
    }

    static readonly Wild[] Specials = [Wild.Plain, Wild.Plain, Wild.Mirror, Wild.Double, Wild.Chameleon];
    static readonly TimeSpan BotDelay = TimeSpan.FromSeconds(1.5), OfflineGrace = TimeSpan.FromSeconds(5);

    public static Tile NewWild(int id, Wild w) => new() { Id = id, Color = -1, Wild = w };

    static int Value(Tile t) => t.Wild == Wild.None ? t.Num : WildValue;

    // Group: same number, all colors different. Run: same color, consecutive numbers.
    // Wilds fill gaps: Plain and Chameleon take 1 slot, Double takes 1 or 2.
    // A Chameleon lets a run switch color once, at the slot it occupies.
    public static bool Valid(List<Tile> set) => Why(set) is null;

    // Why a set is invalid (short text shown above it on the table), or null when it's fine.
    public static string? Why(List<Tile> set)
    {
        var real = set.Where(t => t.Wild == Wild.None).ToList();
        if (set.Any(t => t.Wild == Wild.Mirror)) return WhyMirror(set, real);
        int minSlots = set.Count, maxSlots = set.Count + set.Count(t => t.Wild == Wild.Double);
        if (minSlots < 3) return "precisa de pelo menos 3 peças";
        if (minSlots > MaxNum) return $"máximo de {MaxNum} peças";
        if (real.Count <= 1) return null; // only wilds around one tile: always fits somewhere

        var colors = real.GroupBy(t => t.Color).Select(g => g.ToList()).OrderBy(g => g.Min(t => t.Num)).ToList();
        if (real.All(t => t.Num == real[0].Num))
            return colors.Count != real.Count ? "trinca com cor repetida"
                : minSlots > Colors ? $"trinca vai até {Colors} peças"
                : null;

        var nums = real.Select(t => t.Num).ToList();
        if (nums.Distinct().Count() != nums.Count) return "número repetido na sequência";
        if (colors.Count > 2 || (colors.Count == 2 && !set.Any(t => t.Wild == Wild.Chameleon))) return "sequência com cores misturadas";
        if (colors.Count == 2 && colors[0].Max(t => t.Num) + 1 >= colors[1].Min(t => t.Num))
            return "camaleão precisa de um número livre entre as cores"; // the slot it occupies
        var missing = nums.Max() - nums.Min() + 1 - maxSlots;
        return missing > 0 ? (missing == 1 ? "falta 1 número na sequência" : $"faltam {missing} números na sequência") : null;
    }

    // Mirror sits in the middle; each side is the same set reflected, using both copies of every tile: 3 4 5 🪞 5 4 3.
    static string? WhyMirror(List<Tile> set, List<Tile> real)
    {
        if (set.Count - real.Count != 1) return "espelho não junta com outros especiais";
        if (real.Count < 2) return "precisa de pelo menos 3 peças";
        var pairs = real.GroupBy(t => (t.Num, t.Color)).ToList();
        if (pairs.Any(g => g.Count() != 2)) return "espelho precisa da cópia igual de cada peça";
        var half = pairs.Select(g => g.First()).ToList();
        if (half.All(t => t.Num == half[0].Num)) return null; // pairs are distinct (num, color), so colors differ
        if (half.Select(t => t.Color).Distinct().Count() > 1) return "cada lado do espelho precisa ser uma combinação";
        var nums = half.Select(t => t.Num).ToList();
        return nums.Max() - nums.Min() == nums.Count - 1 ? null : "sequência do espelho tem buraco";
    }

    // Validity and owner of each set while a player is still moving tiles; their hand tiles count as theirs.
    public static List<SetCheck> Preview(Room r, Player p, List<List<int>>? layout)
    {
        var known = r.Table.SelectMany(s => s)
            .Concat(p.Hand.Select(t => new Tile { Id = t.Id, Num = t.Num, Color = t.Color, Wild = t.Wild, Placer = p.Seat }))
            .ToDictionary(t => t.Id);
        return [.. (layout ?? []).Select(ids =>
        {
            var set = (ids ?? []).Where(known.ContainsKey).Select(id => known[id]).ToList();
            if (set.Count == 0 || set.Count != ids!.Count) return new SetCheck(false, -1, "peça desconhecida");
            var why = Why(set);
            return new SetCheck(why is null, Owner(set), why);
        })];
    }

    // Whoever placed the most tiles in the set owns it; a tie means nobody does.
    public static int Owner(List<Tile> set)
    {
        var counts = set.GroupBy(t => t.Placer).Select(g => (Seat: g.Key, N: g.Count())).OrderByDescending(x => x.N).ToList();
        return counts.Count > 1 && counts[0].N == counts[1].N ? -1 : counts[0].Seat;
    }

    public static int TablePoints(Room r, Player p) => Points(r.Table, p.Seat);

    static int Points(List<List<Tile>> table, int seat) => table.Where(s => Owner(s) == seat).Sum(s => s.Sum(Value));

    public static int Score(Room r, Player p) => TablePoints(r, p) - p.Hand.Sum(Value) + (r.Finisher == p.Seat ? 10 : 0);

    public static void Deal(Room r, Mode mode)
    {
        var wilds = Enumerable.Range(0, mode.WildSets * Specials.Length).Select(i => NewWild(1000 + i, Specials[i % Specials.Length]));
        var bag = Enumerable.Range(0, 2 * Colors * MaxNum)
            .Select(i => new Tile { Id = i, Num = i % MaxNum + 1, Color = i / MaxNum % Colors })
            .Concat(wilds).ToArray();
        Random.Shared.Shuffle(bag);
        r.Bag = [.. bag];
        foreach (var p in r.Players) { p.Hand = r.Bag.GetRange(0, mode.HandSize); r.Bag.RemoveRange(0, mode.HandSize); p.Steals = 0; }
        r.Mode = mode;
        r.BestSteal = default;
        r.BestPlay = default;
        r.Table = [];
        r.Fresh = [];
        r.Passes = 0;
        r.Finisher = -1;
        r.Over = false;
        r.Started = true;
        r.Turn = Random.Shared.Next(r.Players.Count);
        Next(r, $"Partida {mode.Name.ToLowerInvariant()} começou!", "start");
    }

    // Applies a proposed table layout (tile ids per set). Returns an error, or null on success.
    public static string? Play(Room r, Player p, List<List<int>>? layout)
    {
        if (layout is null || layout.Any(s => s is null)) return "Jogada inválida.";
        var onTable = r.Table.SelectMany(s => s).ToDictionary(t => t.Id);
        var inHand = p.Hand.ToDictionary(t => t.Id);
        var used = layout.SelectMany(s => s).ToList();
        var usedSet = used.ToHashSet();
        if (usedSet.Count != used.Count) return "Peça repetida na mesa.";
        if (!usedSet.All(id => onTable.ContainsKey(id) || inHand.ContainsKey(id))) return "Essa peça não é sua.";
        if (!onTable.Keys.All(usedSet.Contains)) return "Peças da mesa não voltam pra mão.";
        var placed = used.Where(inHand.ContainsKey).Select(id => inHand[id]).ToList();
        if (placed.Count == 0) return "Baixe pelo menos uma peça, ou compre.";
        var table = layout.Where(s => s.Count > 0)
            .Select(s => s.Select(id => onTable.GetValueOrDefault(id) ?? inHand[id]).ToList()).ToList();
        if (table.Select(Why).FirstOrDefault(w => w != null) is { } why) return $"Combinação inválida: {why}.";

        var before = r.Players.Select(x => TablePoints(r, x)).ToList();
        foreach (var t in placed) { t.Placer = p.Seat; p.Hand.Remove(t); }
        r.Table = table;
        r.Fresh = [.. placed.Select(t => t.Id)];
        r.Passes = 0;
        var losses = r.Players.Where(x => x != p).Select(x => (x.Name, Pts: before[x.Seat] - TablePoints(r, x))).Where(l => l.Pts > 0).ToList();
        var lost = string.Join(" e ", losses.Select(l => $"{l.Pts} pts de {l.Name}"));
        if (losses.Count > 0) p.Steals++;
        if (losses.Sum(l => l.Pts) > r.BestSteal.Pts) r.BestSteal = (losses.Sum(l => l.Pts), $"😈 Maior roubo: {p.Name} tirou {lost}");
        if (placed.Count > r.BestPlay.Tiles) r.BestPlay = (placed.Count, $"🃏 Maior jogada: {p.Name} baixou {placed.Count} peças de uma vez");

        if (p.Hand.Count == 0) { r.Finisher = p.Seat; End(r, $"{p.Name} esvaziou a mão!"); }
        else if (losses.Count > 0) Next(r, $"😈 {p.Name} tirou {lost}!", "steal");
        else Next(r, $"{p.Name} baixou {placed.Count} peça(s).", "play");
        return null;
    }

    // The room owner (seat 0: whoever created it; only bots can be removed, so it never moves) ends the game early.
    // Current scores become the result, with the usual podium.
    public static string? Stop(Room r, Player p)
    {
        if (p.Seat != 0) return "Só o dono da sala pode encerrar a partida.";
        if (!r.Started || r.Over) return "Não tem partida rolando.";
        End(r, $"⛔ {p.Name} encerrou a partida.");
        return null;
    }

    public static string? Draw(Room r, Player p)
    {
        r.Fresh = [];
        if (r.Bag.Count > 0)
        {
            p.Hand.Add(r.Bag[^1]);
            r.Bag.RemoveAt(r.Bag.Count - 1);
            Next(r, $"{p.Name} comprou uma peça.", "draw");
        }
        else if (++r.Passes >= r.Players.Count) End(r, "Ninguém mais consegue jogar.");
        else Next(r, $"{p.Name} passou a vez.", "draw");
        return null;
    }

    // Clock-driven moves: a bot plays after a short pause; a turn that runs out (or whose player dropped) auto-draws.
    // Returns true when the room changed.
    public static bool Tick(Room r, DateTime now)
    {
        if (!r.Started || r.Over) return false;
        var p = r.Players[r.Turn];
        var elapsed = now - r.TurnStarted;
        if (p.IsBot)
        {
            if (elapsed < BotDelay) return false;
            BotMove(r, p);
            return true;
        }
        if (elapsed < TimeSpan.FromSeconds(r.Mode.TurnSeconds) && (p.ConnId != null || elapsed < OfflineGrace)) return false;
        Draw(r, p);
        r.Log = "⏰ " + r.Log;
        return true;
    }

    public static void BotMove(Room r, Player p)
    {
        if (Plan(r, p, p.Level) is not { } layout || Play(r, p, layout) != null) Draw(r, p);
    }

    // Turn planner shared by bots and the hint button. Returns the new table layout, or null when nothing can be laid.
    // 1 easy: one set or tile per turn, no specials. 2 medium: lays everything it can, wilds complete pairs.
    // 3 hard: also borrows a tile from any set that stays valid without it, and at each step picks the move that
    //   best shifts points its way (steals included).
    // ponytail: greedy one step at a time, not a full search; enough to steal on purpose without stalling the server lock
    public static List<List<int>>? Plan(Room r, Player p, int level)
    {
        var move = new Move(
            [.. r.Table.Select(s => s.ToList())],
            [.. p.Hand.Where(t => level > 1 || t.Wild == Wild.None)
                .Select(t => new Tile { Id = t.Id, Num = t.Num, Color = t.Color, Wild = t.Wild, Placer = p.Seat })]);
        var start = move.Hand.Count;
        while (move.Hand.Count > 0)
        {
            var moves = Moves(move, borrow: level == 3, wilds: level > 1);
            var now = Utility(move, p.Seat);
            var next = level < 3 ? moves.FirstOrDefault()
                : moves.Select(m => (m, Gain: Utility(m, p.Seat) - now)).Where(x => x.Gain > 0) // never just feeds a rival
                    .OrderByDescending(x => x.Gain).ThenBy(x => x.m.Hand.Count).Select(x => x.m).FirstOrDefault();
            if (next is null) break;
            move = next;
            if (level == 1) break;
        }
        return move.Hand.Count == start ? null : [.. move.Table.Select(s => s.Select(t => t.Id).ToList())];
    }

    // How good a position is for `seat`: own points on the table, minus everyone else's, minus what's stuck in hand,
    // plus the bonus for emptying it.
    static int Utility(Move m, int seat)
    {
        var others = m.Table.SelectMany(s => s).Select(t => t.Placer).Where(s => s >= 0 && s != seat).Distinct();
        return Points(m.Table, seat) - others.Sum(o => Points(m.Table, o)) - m.Hand.Sum(Value) + (m.Hand.Count == 0 ? 10 : 0);
    }

    // Every single step from a position: a new set from the hand, one hand tile added to a table set, or
    // (borrow) a new set built around a tile taken from a table set that stays valid without it.
    static IEnumerable<Move> Moves(Move m, bool borrow, bool wilds)
    {
        static List<Tile> Without(List<Tile> hand, List<Tile> used) => [.. hand.Where(t => !used.Contains(t))];
        static List<List<Tile>> Swap(List<List<Tile>> table, int i, List<Tile> set) => [.. table.Select((s, j) => j == i ? set : s)];

        foreach (var set in HandSets(m.Hand, wilds).Where(Valid))
            yield return new([.. m.Table, set], Without(m.Hand, set));
        foreach (var t in m.Hand)
            for (var i = 0; i < m.Table.Count; i++)
                if (Valid([.. m.Table[i], t]))
                    yield return new(Swap(m.Table, i, [.. m.Table[i], t]), Without(m.Hand, [t]));
        if (!borrow) yield break;
        for (var i = 0; i < m.Table.Count; i++)
            foreach (var x in m.Table[i])
            {
                List<Tile> rest = [.. m.Table[i].Where(y => y != x)];
                if (!Valid(rest)) continue;
                foreach (var set in HandSets([.. m.Hand, x], wilds: false).Where(s => s.Contains(x) && s.Count >= 3 && Valid(s)))
                    yield return new([.. Swap(m.Table, i, rest), set], Without(m.Hand, set));
            }
    }

    // Candidate sets from a hand: one group per number, one run per streak of consecutive numbers,
    // and (wilds) any two real tiles plus one Plain, Double or Chameleon.
    static IEnumerable<List<Tile>> HandSets(List<Tile> hand, bool wilds)
    {
        var real = hand.Where(t => t.Wild == Wild.None).ToList();
        foreach (var g in real.GroupBy(t => t.Num)) yield return [.. g.DistinctBy(t => t.Color)];
        foreach (var g in real.GroupBy(t => t.Color))
        {
            var run = new List<Tile>();
            foreach (var t in g.DistinctBy(t => t.Num).OrderBy(t => t.Num))
            {
                if (run.Count > 0 && t.Num != run[^1].Num + 1) { yield return run; run = []; }
                run.Add(t);
            }
            yield return run;
        }
        if (!wilds) yield break;
        foreach (var w in hand.Where(t => t.Wild is Wild.Plain or Wild.Double or Wild.Chameleon))
            for (var i = 0; i < real.Count; i++)
                for (var j = i + 1; j < real.Count; j++)
                    yield return [real[i], real[j], w];
    }

    static void Next(Room r, string log, string kind)
    {
        r.Turn = (r.Turn + 1) % r.Players.Count;
        r.TurnStarted = DateTime.UtcNow;
        r.Log = $"{log} Vez de {r.Players[r.Turn].Name}.";
        r.Kind = kind;
        r.Seq++;
    }

    static void End(Room r, string log)
    {
        r.Over = true;
        var winner = r.Players.MaxBy(p => Score(r, p))!;
        r.Log = $"{log} {winner.Name} venceu com {Score(r, winner)} pontos! 🎉";
        r.Kind = "end";
        r.Seq++;
    }

    public static void SelfTest()
    {
        static Tile T(int n, int c, int seat = -1) => new() { Id = n * 10 + c, Num = n, Color = c, Placer = seat };
        Tile J1 = NewWild(1000, Wild.Plain), J2 = NewWild(1001, Wild.Plain);
        Tile M = NewWild(1002, Wild.Mirror), D = NewWild(1003, Wild.Double), C = NewWild(1004, Wild.Chameleon);
        Check(Valid([T(5, 0), T(5, 1), T(5, 2)]), "trinca");
        Check(!Valid([T(5, 0), T(5, 0), T(5, 1)]), "trinca com cor repetida");
        Check(Valid([T(4, 3), T(3, 3), T(5, 3)]), "sequência fora de ordem");
        Check(!Valid([T(3, 1), T(5, 1), T(6, 1)]), "sequência com buraco");
        Check(!Valid([T(1, 1), T(2, 1)]), "menos de 3 peças");
        Check(Valid([T(3, 1), J1, T(5, 1)]), "coringa no buraco da sequência");
        Check(Valid([J1, J2, T(10, 2)]), "coringas antes do 10");
        Check(!Valid([T(3, 1), J1, T(6, 1)]), "um coringa não cobre dois buracos");
        Check(Valid([T(5, 0), T(5, 1), J1]), "coringa na trinca");
        Check(!Valid([T(5, 0), T(5, 1), T(5, 2), T(5, 3), T(5, 4), J1]), "trinca com mais de 5");
        Check(!Valid([T(3, 0), J1, T(5, 2)]), "duas cores sem camaleão");
        Check(Valid([T(3, 1), D, T(6, 1)]), "duplo cobre dois buracos");
        Check(Valid([T(5, 0), T(5, 1), D]), "duplo vale um na trinca");
        Check(Valid([T(3, 0), T(4, 0), C, T(6, 2), T(7, 2)]), "camaleão troca a cor");
        Check(!Valid([T(3, 0), T(4, 0), C, T(5, 2)]), "camaleão precisa de uma posição entre as cores");
        Check(Valid([T(3, 0), T(4, 0), M, T(4, 0), T(3, 0)]), "espelho de sequência");
        Check(Valid([T(7, 0), T(7, 3), M, T(7, 3), T(7, 0)]), "espelho de trinca");
        Check(!Valid([T(3, 0), T(5, 0), M, T(5, 0), T(3, 0)]), "espelho com buraco");
        Check(!Valid([T(3, 0), T(4, 0), M, T(4, 0), T(3, 1)]), "espelho sem par");
        Check(Valid([T(5, 2), M, T(5, 2)]), "espelho com uma peça de cada lado");
        Check(!Valid([T(5, 2), M, T(5, 1)]), "espelho de uma peça precisa da cópia igual");
        Check(Why([T(3, 1), J1, T(6, 1)]) == "falta 1 número na sequência", "motivo: buraco");
        Check(Why([T(5, 2), M, T(5, 1)]) == "espelho precisa da cópia igual de cada peça", "motivo: espelho");
        Check(Why([T(3, 0), T(4, 1), T(5, 0)]) == "sequência com cores misturadas", "motivo: cores");
        Check(Owner([T(1, 0, 0), T(2, 0, 0), T(3, 0, 1)]) == 0, "dono pela maioria");
        Check(Owner([T(1, 0, 0), T(2, 0, 1)]) == -1, "empate fica sem dono");

        // A rouba: tira o 6 da sequência do B e forma 6-7-8 com as próprias peças
        var r = new Room { Code = "TEST", Started = true };
        var a = new Player { Token = "a", Name = "A", Seat = 0 };
        var b = new Player { Token = "b", Name = "B", Seat = 1 };
        r.Players.AddRange([a, b]);
        r.Table = [[T(3, 0, 1), T(4, 0, 1), T(5, 0, 1), T(6, 0, 1)]];
        a.Hand = [T(7, 0), T(8, 0), T(1, 4)];
        b.Hand = [T(9, 4)];
        Check(Play(r, a, [[30], [40, 50, 60, 70, 80]]) != null, "recusa combinação inválida");
        Check(Play(r, a, [[40, 50, 60, 70]]) != null, "peça da mesa não pode sumir");
        Check(Play(r, a, [[30, 40, 50], [60, 70, 80]]) == null, "jogada válida");
        Check(Owner(r.Table[0]) == 1 && Owner(r.Table[1]) == 0, "roubo");
        Check(r.Kind == "steal" && r.Fresh.SetEquals([70, 80]), "anuncia o roubo e destaca as peças novas");
        Check(r.BestSteal.Pts == 6 && r.BestPlay.Tiles == 2 && a.Steals == 1, "melhores momentos");
        Check(Score(r, a) == 21 - 1 && Score(r, b) == 12 - 9, "pontuação");

        // bot baixa a sequência que tem na mão
        var rb = new Room { Code = "BOT", Started = true };
        var bot = new Player { Token = "c", Name = "Bot", Seat = 0, IsBot = true };
        var human = new Player { Token = "d", Name = "D", Seat = 1, ConnId = "x" };
        rb.Players.AddRange([bot, human]);
        bot.Hand = [T(3, 0), T(4, 0), T(5, 0), T(9, 1)];
        rb.Table = [[T(6, 2, 1), T(7, 2, 1), T(8, 2, 1)]];
        BotMove(rb, bot);
        Check(bot.Hand.Count == 1 && rb.Table.Count == 2 && rb.Turn == 1, "bot baixa sequência");

        // bot difícil rouba de propósito: pega o 6 da sequência do humano pra formar 6-7-8;
        // o médio só gruda o 7 e o 8 na sequência dos outros (que continua deles)
        Room StealSetup(int level, out Player me)
        {
            var room = new Room { Code = "HARD", Started = true };
            me = new Player { Token = "e", Name = "Bot", Seat = 0, IsBot = true, Level = level };
            room.Players.AddRange([me, new Player { Token = "f", Name = "F", Seat = 1, ConnId = "y" }]);
            room.Table = [[T(3, 0, 1), T(4, 0, 1), T(5, 0, 1), T(6, 0, 1)]];
            me.Hand = [T(7, 0), T(8, 0), T(1, 4)];
            return room;
        }
        var hard = StealSetup(3, out var hardBot);
        BotMove(hard, hardBot);
        Check(hard.Table.Count == 2 && hard.Table.Any(s => Owner(s) == 0), "bot difícil rouba");
        var medium = StealSetup(2, out var mediumBot);
        BotMove(medium, mediumBot);
        Check(medium.Table.Count == 1 && Owner(medium.Table[0]) == 1, "bot médio não rouba");
        // difícil não gruda peça na combinação de outro só pra se livrar dela
        var feed = new Room { Code = "FEED", Started = true };
        var hb = new Player { Token = "g", Name = "Bot", Seat = 0, IsBot = true, Level = 3 };
        feed.Players.AddRange([hb, new Player { Token = "h", Name = "H", Seat = 1, ConnId = "z" }]);
        feed.Table = [[T(4, 0, 1), T(4, 1, 1), T(4, 2, 1)]];
        hb.Hand = [T(4, 3), T(9, 4)];
        Check(Plan(feed, hb, 3) is null && Plan(feed, hb, 2) != null, "difícil não dá pontos de graça");

        // tirar o bot do meio: o de trás sobe de lugar e troca de nome; humano não sai
        var lobby = new Room { Code = "KICK" };
        lobby.Players.AddRange([
            new Player { Token = "i", Name = "Jeff", Seat = 0 },
            new Player { Token = "j", Name = "Bot Coruja", Seat = 1, IsBot = true },
            new Player { Token = "k", Name = "Bot Sapo", Seat = 2, IsBot = true }]);
        Check(RemoveBot(lobby, 0) != null && RemoveBot(lobby, 1) == null, "só bot sai");
        Check(lobby.Players.Count == 2 && lobby.Players[1] is { Seat: 1, Name: "Bot Coruja" }, "bot de trás sobe de lugar");

        // só o dono (lugar 0) encerra, e só com partida rolando
        Check(Stop(lobby, lobby.Players[0]) != null, "não encerra sem partida");
        lobby.Started = true;
        Check(Stop(lobby, lobby.Players[1]) != null && !lobby.Over, "só o dono encerra");
        Check(Stop(lobby, lobby.Players[0]) == null && lobby.Over && lobby.Kind == "end", "dono encerra");

        // sair: no meio da partida o lugar fica (offline); fora dela, o lugar é liberado e o de trás sobe
        var leaving = new Room { Code = "SAIR", Started = true };
        var host = new Player { Token = "l", Name = "Jeff", Seat = 0, ConnId = "c1" };
        var guest = new Player { Token = "m", Name = "Ana", Seat = 1, ConnId = "c2" };
        leaving.Players.AddRange([host, guest]);
        Leave(leaving, host);
        Check(leaving.Players.Count == 2 && host.ConnId == null, "sair no meio da partida guarda o lugar");
        leaving.Over = true;
        Leave(leaving, host);
        Check(leaving.Players is [{ Name: "Ana", Seat: 0 }] && !leaving.Started, "sair depois libera o lugar e a Ana vira dona");

        var hinted = StealSetup(2, out var asker);
        Check(Plan(hinted, asker, 3) != null && hinted.Table.Count == 1 && asker.Hand.Count == 3 && hinted.Table[0].All(t => t.Placer == 1),
            "dica sugere sem mexer na sala");

        // humano estoura o tempo e compra sozinho
        rb.Bag = [T(1, 3)];
        rb.TurnStarted = DateTime.UtcNow.AddMinutes(-5);
        Check(Tick(rb, DateTime.UtcNow) && human.Hand.Count == 1 && rb.Turn == 0, "tempo esgotado compra sozinho");
        Console.WriteLine("selftest ok");
    }

    static void Check(bool ok, string what) { if (!ok) throw new Exception("falhou: " + what); }
}

class GameHub : Hub
{
    static readonly Dictionary<string, Room> Rooms = [];
    static readonly object Gate = new(); // ponytail: one lock for every room, per-room locks if it ever hosts lots of games
    // ponytail: rooms live in memory and vanish on restart; idle ones expire and the count is capped,
    // enough to keep a public link from filling memory. A real database if games must survive restarts.
    const int MaxRooms = 200;
    static readonly TimeSpan RoomTtl = TimeSpan.FromHours(3);
    static readonly string[] Reactions = ["😂", "😱", "👏", "😈", "🤔", "❤️"];

    public async Task<string> Create(string token, string name)
    {
        string code;
        lock (Gate)
        {
            foreach (var old in Rooms.Values.Where(r => DateTime.UtcNow - r.LastActive > RoomTtl).ToList()) Rooms.Remove(old.Code);
            if (Rooms.Count >= MaxRooms) throw new HubException("Servidor cheio, tenta de novo daqui a pouco.");
            do code = new string(Random.Shared.GetItems<char>("ABCDEFGHJKLMNPQRSTUVWXYZ", 4)); while (Rooms.ContainsKey(code));
            Rooms[code] = new Room { Code = code };
        }
        await Join(code, token, name);
        return code;
    }

    public async Task<string?> Join(string code, string token, string name)
    {
        Room? r;
        lock (Gate)
        {
            if (!Rooms.TryGetValue(code?.Trim().ToUpperInvariant() ?? "", out r)) return "Sala não encontrada.";
            var p = r.Players.Find(x => x.Token == token);
            if (p is null)
            {
                if (r.Started && !r.Over) return "A partida já começou.";
                if (r.Players.Count >= 4) return "Sala cheia (máx. 4).";
                name = name?.Trim() ?? "";
                p = new Player { Token = token, Name = name.Length == 0 ? "Jogador" : name[..Math.Min(name.Length, 16)], Seat = r.Players.Count };
                r.Players.Add(p);
            }
            p.ConnId = Context.ConnectionId;
        }
        await Push(Clients, r);
        return null;
    }

    public Task<string?> AddBot(int level) => Act((r, p) =>
    {
        if (r.Started && !r.Over) return "A partida já está rolando.";
        if (r.Players.Count >= 4) return "Sala cheia (máx. 4).";
        r.Players.Add(new Player
        {
            Token = Guid.NewGuid().ToString(), Name = $"Bot {Rules.Animals[r.Players.Count]}", Seat = r.Players.Count,
            IsBot = true, Level = Math.Clamp(level, 1, 3),
        });
        return null;
    });

    public Task<string?> RemoveBot(int seat) => Act((r, p) => Rules.RemoveBot(r, seat));

    public Task<string?> EndGame() => Act(Rules.Stop);

    public async Task Leave()
    {
        Room? room;
        lock (Gate)
        {
            if (Mine() is not { } m) return;
            room = m.Room;
            Rules.Leave(room, m.Player);
            if (room.Players.All(p => p.IsBot)) { Rooms.Remove(room.Code); room = null; } // only bots left: nobody to play for
        }
        if (room != null) await Push(Clients, room);
    }

    // The hard bot's plan for your hand, shown as a suggestion; it changes nothing on the server.
    public List<List<int>>? Hint()
    {
        lock (Gate) return Mine() is { } m && MyTurn(m.Room, m.Player) is null ? Rules.Plan(m.Room, m.Player, 3) : null;
    }

    public Task<string?> Start(string mode) => Act((r, p) =>
    {
        if (r.Started && !r.Over) return "A partida já está rolando.";
        if (r.Players.Count < 2) return "Precisa de pelo menos 2 jogadores (dá pra chamar um bot).";
        Rules.Deal(r, Rules.Modes.GetValueOrDefault(mode ?? "") ?? Rules.Modes["normal"]);
        return null;
    });

    public Task<string?> SetTheme(string theme) => Act((r, p) =>
    {
        if (r.Started && !r.Over) return "Dá pra trocar o tema só entre partidas.";
        if (!Rules.Themes.Contains(theme)) return "Tema desconhecido.";
        r.Theme = theme;
        return null;
    });

    public Task<string?> Play(List<List<int>> layout) => Act((r, p) => MyTurn(r, p) ?? Rules.Play(r, p, layout));

    public Task<string?> Draw() => Act((r, p) => MyTurn(r, p) ?? Rules.Draw(r, p));

    public List<SetCheck> Preview(List<List<int>> layout)
    {
        lock (Gate) return Mine() is { } m ? Rules.Preview(m.Room, m.Player, layout) : [];
    }

    public async Task React(string emoji)
    {
        if (!Reactions.Contains(emoji)) return;
        int seat;
        List<string> conns;
        lock (Gate)
        {
            if (Mine() is not { } m) return;
            seat = m.Player.Seat;
            conns = [.. m.Room.Players.Select(x => x.ConnId).OfType<string>()];
        }
        await Clients.Clients(conns).SendAsync("react", seat, emoji);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Room? room;
        lock (Gate)
        {
            var m = Mine();
            if (m is { } x) x.Player.ConnId = null;
            room = m?.Room;
        }
        if (room != null) await Push(Clients, room);
    }

    public static async Task Tick(IHubClients<IClientProxy> clients)
    {
        List<Room> changed;
        lock (Gate) changed = [.. Rooms.Values.Where(r => Rules.Tick(r, DateTime.UtcNow))];
        foreach (var r in changed) await Push(clients, r);
    }

    static string? MyTurn(Room r, Player p) => r.Started && !r.Over && r.Turn == p.Seat ? null : "Não é sua vez.";

    (Room Room, Player Player)? Mine()
    {
        foreach (var r in Rooms.Values)
            if (r.Players.Find(x => x.ConnId == Context.ConnectionId) is { } p) return (r, p);
        return null;
    }

    async Task<string?> Act(Func<Room, Player, string?> action)
    {
        Room room;
        string? err;
        lock (Gate)
        {
            if (Mine() is not { } m) return "Você não está numa sala.";
            (room, err) = (m.Room, action(m.Room, m.Player));
        }
        if (err is null) await Push(Clients, room);
        return err;
    }

    // Each player gets their own view: everyone sees the table, only you see your hand.
    static async Task Push(IHubClients<IClientProxy> clients, Room r)
    {
        List<(string Conn, object View)> views;
        lock (Gate)
        {
            r.LastActive = DateTime.UtcNow;
            views = [.. r.Players.Where(p => p.ConnId != null).Select(p => (p.ConnId!, View(r, p)))];
        }
        foreach (var (conn, view) in views) await clients.Client(conn).SendAsync("state", view);
    }

    static List<string> Highlights(Room r)
    {
        var thief = r.Players.MaxBy(p => p.Steals);
        return [.. new[] { r.BestSteal.Text, r.BestPlay.Text, thief?.Steals > 1 ? $"🦹 Mais roubos: {thief.Name} ({thief.Steals})" : null }.OfType<string>()];
    }

    static object View(Room r, Player me)
    {
        static object Snap(Tile t) => new { t.Id, t.Num, t.Color, t.Wild, t.Placer };
        var playing = r.Started && !r.Over;
        return new
        {
            r.Code, You = me.Seat, r.Turn, r.Started, r.Over, Bag = r.Bag.Count, r.Log, r.Kind, r.Seq,
            Mode = r.Mode.Name, r.Theme,
            Highlights = r.Over ? Highlights(r) : [],
            TurnLeftMs = playing ? Math.Max(0, (int)(r.TurnStarted.AddSeconds(r.Mode.TurnSeconds) - DateTime.UtcNow).TotalMilliseconds) : 0,
            Fresh = r.Fresh.ToList(),
            Players = r.Players.Select(p => new
            {
                p.Name, p.IsBot, Level = p.IsBot ? p.Level : 0, Online =p.IsBot || p.ConnId != null, Hand = p.Hand.Count,
                Score = r.Over ? Rules.Score(r, p) : Rules.TablePoints(r, p),
            }).ToList(),
            Table = r.Table.Select(s => new { Tiles = s.Select(Snap).ToList(), Owner = Rules.Owner(s) }).ToList(),
            Hand = me.Hand.Select(Snap).ToList(),
        };
    }
}
