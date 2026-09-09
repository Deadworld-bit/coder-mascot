// Geometry checks for the mascot's screen-edge patrol.
//
// The app is WPF and only runs on Windows, so this is the one part of the
// patrol behaviour that can actually be verified here. Corners, wrapping and
// the snap-back-after-drag projection are exactly where an off-by-one hides.
//
// Run:  dotnet run -c Release      (exit code = number of failures)

using CoderMascot.Core;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok) failures++;
}

static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) < eps;

// A 1920x1080 screen at the origin, and an off-origin one to catch code that
// assumes (0,0) — a second monitor to the left gives negative coordinates.
(double L, double T, double W, double H)[] screens =
[
    (0, 0, 1920, 1080),
    (-1920, -200, 2560, 1440),
];

foreach (var (l, t, w, h) in screens)
{
    var tag = $"[{w}x{h} @ {l},{t}]";
    var p = Perimeter.Length(w, h);

    Check($"{tag} perimeter = 2(w+h)", Near(p, 2 * (w + h)));

    // --- the four corners land exactly where they should ---
    var tl = Perimeter.Resolve(0, l, t, w, h);
    var tr = Perimeter.Resolve(w, l, t, w, h);
    var br = Perimeter.Resolve(w + h, l, t, w, h);
    var bl = Perimeter.Resolve(2 * w + h, l, t, w, h);

    Check($"{tag} s=0 is the top-left corner", Near(tl.X, l) && Near(tl.Y, t));
    Check($"{tag} s=w is the top-right corner", Near(tr.X, l + w) && Near(tr.Y, t));
    Check($"{tag} s=w+h is the bottom-right corner", Near(br.X, l + w) && Near(br.Y, t + h));
    Check($"{tag} s=2w+h is the bottom-left corner", Near(bl.X, l) && Near(bl.Y, t + h));
    Check($"{tag} s=perimeter wraps back to the start",
        Near(Perimeter.Resolve(p, l, t, w, h).X, tl.X) &&
        Near(Perimeter.Resolve(p, l, t, w, h).Y, tl.Y));

    // --- each quarter reports the edge it's actually on ---
    Check($"{tag} mid-top is Top", Perimeter.Resolve(w / 2, l, t, w, h).Edge == Edge.Top);
    Check($"{tag} mid-right is Right", Perimeter.Resolve(w + h / 2, l, t, w, h).Edge == Edge.Right);
    Check($"{tag} mid-bottom is Bottom", Perimeter.Resolve(w + h + w / 2, l, t, w, h).Edge == Edge.Bottom);
    Check($"{tag} mid-left is Left", Perimeter.Resolve(2 * w + h + h / 2, l, t, w, h).Edge == Edge.Left);

    // --- walking the whole loop never teleports ---
    // A discontinuity at a corner is the classic bug here and would show up as
    // the mascot visibly jumping across the screen.
    var maxJump = 0.0;
    var prev = Perimeter.Resolve(0, l, t, w, h);
    for (var s = 1.0; s <= p; s += 1.0)
    {
        var cur = Perimeter.Resolve(s, l, t, w, h);
        maxJump = Math.Max(maxJump, Math.Abs(cur.X - prev.X) + Math.Abs(cur.Y - prev.Y));
        prev = cur;
    }
    Check($"{tag} no jump > 1.001px anywhere on the loop (max {maxJump:F4})", maxJump <= 1.001);

    // --- every point stays on the rectangle's border ---
    var offEdge = 0;
    for (var s = 0.0; s < p; s += 7.0)
    {
        var q = Perimeter.Resolve(s, l, t, w, h);
        var onBorder = Near(q.X, l) || Near(q.X, l + w) || Near(q.Y, t) || Near(q.Y, t + h);
        var inside = q.X >= l - 1e-9 && q.X <= l + w + 1e-9 && q.Y >= t - 1e-9 && q.Y <= t + h + 1e-9;
        if (!onBorder || !inside) offEdge++;
    }
    Check($"{tag} every sampled point is on the border, never the middle", offEdge == 0);

    // --- round trip: resolve then project back ---
    var worst = 0.0;
    for (var s = 0.0; s < p; s += 13.0)
    {
        var q = Perimeter.Resolve(s, l, t, w, h);
        var back = Perimeter.Nearest(q.X, q.Y, l, t, w, h);
        // Corners belong to two edges, so compare around the loop.
        worst = Math.Max(worst, Math.Abs(Perimeter.Delta(s, back, p)));
    }
    Check($"{tag} Resolve -> Nearest round-trips (worst {worst:F6})", worst < 1e-6);

    // --- snap-back from an arbitrary drop point ---
    var centre = Perimeter.Nearest(l + w / 2, t + h / 2, l, t, w, h);
    var centreEdge = Perimeter.Resolve(centre, l, t, w, h);
    Check($"{tag} dropped in the middle -> snaps to an edge",
        Near(centreEdge.X, l) || Near(centreEdge.X, l + w) ||
        Near(centreEdge.Y, t) || Near(centreEdge.Y, t + h));

    var nearTop = Perimeter.Nearest(l + w / 2, t + 5, l, t, w, h);
    Check($"{tag} dropped near the top -> snaps to Top",
        Perimeter.Resolve(nearTop, l, t, w, h).Edge == Edge.Top);

    var nearLeft = Perimeter.Nearest(l + 5, t + h / 2, l, t, w, h);
    Check($"{tag} dropped near the left -> snaps to Left",
        Perimeter.Resolve(nearLeft, l, t, w, h).Edge == Edge.Left);

    // --- Delta picks the short way round ---
    Check($"{tag} Delta never exceeds half the loop",
        Math.Abs(Perimeter.Delta(0, p / 2 + 10, p)) <= p / 2 + 1e-9);
    Check($"{tag} Delta goes backwards across the origin",
        Perimeter.Delta(10, p - 10, p) < 0);
    Check($"{tag} Delta goes forwards normally", Perimeter.Delta(10, 200, p) > 0);
    Check($"{tag} Delta to self is zero", Near(Perimeter.Delta(500, 500, p), 0));
}

// ---------------------------------------------------------------------------
// Multi-monitor dead space.
//
// The patrol perimeter is the bounding box of all monitors, which only matches
// the monitors when they are identical and aligned. These are the real
// arrangements a review measured as leaving the mascot invisible for ~25s per
// lap — and, worse, sending the disconnect alarm to a corner with no display
// behind it.
// ---------------------------------------------------------------------------

(string Name, (double L, double T, double W, double H)[] Monitors)[] setups =
[
    ("dual, tops aligned",   [(0, 0, 1920, 1080), (1920, 0, 2560, 1440)]),
    ("dual, bottoms aligned",[(0, 0, 1920, 1080), (1920, -360, 2560, 1440)]),
    ("landscape + portrait", [(0, 0, 1920, 1080), (1920, 0, 1080, 1920)]),
    ("second monitor left",  [(-1920, 0, 1920, 1080), (0, 0, 2560, 1440)]),
    ("single",               [(0, 0, 1920, 1080)]),
];

foreach (var (name, mons) in setups)
{
    // Bounding box of every screen — what PatrolEngine.Bounds computes.
    var bl = mons.Min(s => s.L);
    var bt = mons.Min(s => s.T);
    var bw = mons.Max(s => s.L + s.W) - bl;
    var bh = mons.Max(s => s.T + s.H) - bt;
    var loop = Perimeter.Length(bw, bh);

    // How much of the raw perimeter is dead space, before skipping.
    var dead = 0;
    var total = 0;
    for (var s = 0.0; s < loop; s += 4)
    {
        var q = Perimeter.Resolve(s, bl, bt, bw, bh);
        total++;
        if (!Perimeter.OnAnyScreen(q.X, q.Y, mons)) dead++;
    }

    // The real test: walk a full lap through SkipDeadSpace and assert the
    // mascot is on a display at every single step.
    var offScreen = 0;
    var pos = 0.0;
    for (var i = 0; i < 4000; i++)
    {
        pos = Perimeter.SkipDeadSpace(pos, 1, bl, bt, bw, bh, mons);
        var q = Perimeter.Resolve(pos, bl, bt, bw, bh);
        if (!Perimeter.OnAnyScreen(q.X, q.Y, mons)) offScreen++;
        pos = Perimeter.Wrap(pos + 3, loop);
    }

    Check($"{name}: never off-screen while walking ({100.0 * dead / total:F0}% of raw perimeter is dead)",
        offScreen == 0);

    // Same walking backwards — direction must not strand it.
    offScreen = 0;
    pos = 0.0;
    for (var i = 0; i < 4000; i++)
    {
        pos = Perimeter.SkipDeadSpace(pos, -1, bl, bt, bw, bh, mons);
        var q = Perimeter.Resolve(pos, bl, bt, bw, bh);
        if (!Perimeter.OnAnyScreen(q.X, q.Y, mons)) offScreen++;
        pos = Perimeter.Wrap(pos - 3, loop);
    }
    Check($"{name}: never off-screen walking backwards", offScreen == 0);

    // The alert corner must land on a real display, or the disconnect alarm
    // hangs somewhere nobody can see.
    foreach (var host in mons)
    {
        var anchorX = host.L + 80;
        var anchorY = host.T;

        var bestS = -1.0;
        var bestD = double.MaxValue;
        for (var s = 0.0; s < loop; s += 8)
        {
            var q = Perimeter.Resolve(s, bl, bt, bw, bh);
            if (!Perimeter.OnAnyScreen(q.X, q.Y, mons)) continue;
            var d = (q.X - anchorX) * (q.X - anchorX) + (q.Y - anchorY) * (q.Y - anchorY);
            if (d < bestD) { bestD = d; bestS = s; }
        }

        var corner = Perimeter.Resolve(bestS, bl, bt, bw, bh);
        Check($"{name}: alert corner for screen @{host.L},{host.T} is on a display",
            bestS >= 0 && Perimeter.OnAnyScreen(corner.X, corner.Y, mons));
    }
}

Check("SkipDeadSpace terminates when no screen matches at all",
    Near(Perimeter.SkipDeadSpace(0, 1, 0, 0, 100, 100, []), 0));

// --- degenerate inputs must not throw or produce NaN ---
Check("zero-size screen doesn't divide by zero", Near(Perimeter.Wrap(50, 0), 0));
Check("negative s wraps positive", Near(Perimeter.Wrap(-10, 100), 90));
Check("NaN s is contained", Near(Perimeter.Wrap(double.NaN, 100), 0));
Check("Delta with zero period is zero", Near(Perimeter.Delta(1, 2, 0), 0));

var deg = Perimeter.Resolve(5, 0, 0, 0, 0);
Check("zero-size rect resolves without throwing", Near(deg.X, 0) && Near(deg.Y, 0));

// --- poses ---------------------------------------------------------------
//
// None of this can fail loudly at runtime: a wrong answer here is a mascot
// climbing face-first into a wall, or standing upright while it hangs off the
// ceiling. It looks broken and reports nothing.

// --- poses ---------------------------------------------------------------
//
// None of this can fail loudly at runtime: a wrong answer here is a mascot
// climbing face-first into a wall, or a dragon playing a dive while it flies up
// the screen. It looks broken and reports nothing.

const MovementStyle walk = MovementStyle.Walks;
const MovementStyle fly = MovementStyle.Flies;

Check("floor walks", Pose.Animation(walk, Edge.Bottom, true) == "run");
Check("floor rests", Pose.Animation(walk, Edge.Bottom, false) == "idle");
Check("ceiling hangs whether or not it is moving",
    Pose.Animation(walk, Edge.Top, true) == "hang" &&
    Pose.Animation(walk, Edge.Top, false) == "hang");

foreach (var wall in new[] { Edge.Left, Edge.Right })
{
    Check($"{wall} wall climbs", Pose.Animation(walk, wall, true) == "climb");
    Check($"{wall} wall clings", Pose.Animation(walk, wall, false) == "climbidle");

    // The whole point of the wall artwork: for a climber, facing is a property
    // of the wall, not of the journey. Turning around mid-climb must not spin
    // the character to face into the bricks.
    Check($"{wall} wall facing ignores direction (climbing)",
        Pose.Mirrored(walk, wall, 1) == Pose.Mirrored(walk, wall, -1));
}

Check("the two walls face opposite ways",
    Pose.Mirrored(walk, Edge.Left, 1) != Pose.Mirrored(walk, Edge.Right, 1));
Check("left wall uses the art as drawn", !Pose.Mirrored(walk, Edge.Left, 1));

Check("floor turns with travel",
    Pose.Mirrored(walk, Edge.Bottom, 1) && !Pose.Mirrored(walk, Edge.Bottom, -1));
Check("ceiling turns with travel",
    Pose.Mirrored(walk, Edge.Top, -1) && !Pose.Mirrored(walk, Edge.Top, 1));

// Clockwise from the top-left, the floor is travelled right-to-left and the
// ceiling left-to-right, so at the same `dir` they must mirror oppositely — or
// the mascot moonwalks along one of them.
Check("floor and ceiling mirror oppositely at the same heading",
    Pose.Mirrored(walk, Edge.Bottom, 1) != Pose.Mirrored(walk, Edge.Top, 1));

// --- and the same perimeter, flown -----------------------------------------
//
// A flier has no climb and nothing to hang from, so every wall answer differs.
// `dir` is +1 clockwise, which runs DOWN the right-hand wall and UP the left —
// getting that backwards plays a dive while the character rises.

Check("flier rises up the left wall", Pose.GoingUp(Edge.Left, 1));
Check("flier dives down the right wall", !Pose.GoingUp(Edge.Right, 1));
Check("reversing on a wall reverses the climb", Pose.GoingUp(Edge.Right, -1));
Check("the floor is never 'up'", !Pose.GoingUp(Edge.Bottom, 1) && !Pose.GoingUp(Edge.Bottom, -1));

Check("flier ascends the left wall going clockwise",
    Pose.Animation(fly, Edge.Left, true, 1) == "ascend");
Check("flier descends the left wall going back",
    Pose.Animation(fly, Edge.Left, true, -1) == "descend");
Check("flier descends the right wall going clockwise",
    Pose.Animation(fly, Edge.Right, true, 1) == "descend");
Check("flier ascends the right wall going back",
    Pose.Animation(fly, Edge.Right, true, -1) == "ascend");
Check("a stopped flier hovers at a wall", Pose.Animation(fly, Edge.Left, false) == "hover");
Check("a flier crosses the ceiling flying", Pose.Animation(fly, Edge.Top, true) == "fly");
Check("a stopped flier holds station at the ceiling",
    Pose.Animation(fly, Edge.Top, false) == "flyidle");
Check("a flier lands on the floor when it stops",
    Pose.Animation(fly, Edge.Bottom, false) == "idle");

// A flier is in open air, so unlike a climber it faces the way it is going —
// on the walls too.
Check("flier wall facing follows direction",
    Pose.Mirrored(fly, Edge.Left, 1) != Pose.Mirrored(fly, Edge.Left, -1));
Check("both walls agree for a flier at the same heading",
    Pose.Mirrored(fly, Edge.Left, 1) == Pose.Mirrored(fly, Edge.Right, 1));
Check("the two styles genuinely disagree about walls",
    Pose.Mirrored(fly, Edge.Left, 1) != Pose.Mirrored(walk, Edge.Left, 1));

// --- artwork actually exists for every character ---------------------------
//
// A missing folder is not an error anywhere in the app: SpriteSet falls back
// and the mascot quietly spends the session in the wrong pose. Charizard's
// frames aren't cut yet, so a character with no folder at all is skipped —
// but one that is half-present is a real defect and still fails.
var sprites = Path.Combine(AppContext.BaseDirectory, "../../../../../Assets/sprites");

foreach (var character in Character.All)
{
    var root = Path.Combine(sprites, character.Id);
    if (!Directory.Exists(root))
    {
        Console.WriteLine($"SKIP  {character.Id}: no frames cut yet");
        continue;
    }

    foreach (var name in Pose.All(character.Style))
    {
        var dir = Path.Combine(root, name);
        var count = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").Length : 0;
        Check($"{character.Id}/{name} has frames", count > 0);
    }
}

foreach (var style in new[] { walk, fly })
{
    var reachable = new HashSet<string>();
    foreach (var e in new[] { Edge.Top, Edge.Right, Edge.Bottom, Edge.Left })
    foreach (var moving in new[] { true, false })
    foreach (var dir in new[] { 1, -1 })
        reachable.Add(Pose.Animation(style, e, moving, dir));

    Check($"Pose.All({style}) covers everything Animation can return",
        reachable.IsSubsetOf(Pose.All(style)));
}

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures;
