using System;
using System.Diagnostics;
using VeteranSoldiers;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
bool Near(double a, double b) => Math.Abs(a-b) < .0001;
var ranks = Progression.Defaults();
Progression.Validate(ranks);
Check(Progression.Level(0, ranks, 5) == 0, "recruit");
Check(Progression.Level(99.99, ranks, 5) == 0, "below threshold");
Check(Progression.Level(100, ranks, 5) == 1, "exact threshold");
Check(Progression.Level(1500, ranks, 5) == 4, "legendary");
Check(Progression.Level(100000, ranks, 3) == 2, "level cap");
Check(Near(Progression.Fraction(200, ranks, 5), .5), "progress in current rank");
Check(Near(Progression.Fraction(1500, ranks, 5), 1), "max rank progress");
Check(Near(Progression.EffectiveShare(20, -500, 600, 600), 20), "overkill capped to remaining health");
Check(Near(Progression.EffectiveShare(100, 50, 60, 100), 30), "shared simultaneous damage");
Check(Near(Progression.EffectiveShare(100, 110, 10, 10), 0), "healing yields no combat XP");
Check(Near(Progression.EffectiveShare(0, -30, 30, 30), 0), "dead target yields no XP");
Check(Near(Progression.EffectiveShare(100, 50, double.NaN, 10), 0), "invalid contribution rejected");
Check(Near(Progression.Award(100, false, 5, 5, 1, 1), 20), "damage-based XP");
Check(Near(Progression.Award(100, true, 5, 5, 1, 1), 25), "single kill bonus");
Check(Near(Progression.Award(100, false, 5, 5, 1, .5), 10), "siege normalization");
double rapid = 0; for (int i=0;i<10;i++) rapid += Progression.Award(10,false,5,5,1,1);
Check(Near(rapid, Progression.Award(100,false,5,5,1,1)), "attack speed does not increase damage XP");
double archerA = Progression.Award(500,false,5,5,1,1), archerB = 0;
Check(Progression.Level(archerA,ranks,5)==1 && Progression.Level(archerB,ranks,5)==0, "independent progression inputs");
Check(Near(Progression.CombinedHealthFactor(100,.3f,120),1.2), "maximum health cap");
Check(Near(Progression.CombinedHealthFactor(1000,.3f,120),1), "cap does not erase vanilla health");
Check(Near(Progression.CombinedHealthFactor(120,.2f,100000),1.2), "health composes with researched baseline");
bool invalid = false;
try { var bad=Progression.Defaults();bad[2].Threshold=99;Progression.Validate(bad); } catch(ArgumentException){invalid=true;}
Check(invalid,"non-increasing thresholds rejected");
invalid = false;
try { var bad=Progression.Defaults();bad[0].HealthBonus=.1f;Progression.Validate(bad); } catch(ArgumentException){invalid=true;}
Check(invalid,"recruits cannot claim an unapplied configurable health bonus");
VeteranText.Configure("English");
Check(string.Join(",", new[] { VeteranText.RankName(0), VeteranText.RankName(1), VeteranText.RankName(2), VeteranText.RankName(3), VeteranText.RankName(4) }) ==
    "Recruit,Trained,Veteran,Elite,Legendary", "five English rank labels");
Check(VeteranText.Number(1234.5, "N1") == "1,234.5" && VeteranText.Soldiers(1) == "1 soldier" && VeteranText.NextRank("Elite", 1.5) == "Next: Elite\n1.5 XP remaining", "English presentation formatting");
VeteranText.Configure("Spanish");
Check(string.Join(",", new[] { VeteranText.RankName(0), VeteranText.RankName(1), VeteranText.RankName(2), VeteranText.RankName(3), VeteranText.RankName(4) }) ==
    "Recluta,Entrenado,Veterano,Élite,Legendario", "five Spanish rank labels");
Check(VeteranText.Number(1234.5, "N1") == "1.234,5" && VeteranText.Soldiers(2) == "2 soldados" && VeteranText.MaximumRank == "Rango máximo alcanzado", "Spanish presentation formatting");
const string customRank = "Guardia de élite";
Check(VeteranText.ResolveRankName(3, customRank) == customRank, "custom rank name preserved verbatim");
Check(VeteranText.ResolveRankName(3, "  ") == "Élite" && VeteranText.ResolveRankName(3, null) == "Élite", "empty custom names use selected language");
VeteranText.Configure("Unrecognized language");
Check(VeteranText.RankName(3) == "Elite" && VeteranText.Number(1.5, "N1") == "1.5", "invalid language falls back to English");
Check(VeteranText.ResolveRankName(3, customRank) == customRank, "language change never translates a custom rank name");
var promotions = new PromotionQueue();
promotions.Add(0, 1, 10); promotions.Add(0, 3, 10.1); promotions.Add(2, 3, 10.2);
Check(!promotions.Tick(10.3), "promotion burst waits for grouping window");
Check(promotions.Tick(10.7) && promotions.Total == 3 && promotions.HighestRank == 3 && promotions.Count(3) == 2, "mixed promotions aggregate in bounded rank counts");
promotions.Add(3, 3, 11); promotions.Add(4, 1, 11); promotions.Add(0, 5, 11); promotions.Add(0, 1, double.NaN);
Check(promotions.Tick(14) && promotions.Total == 3, "unchanged ranks demotions and invalid events never become promotions");
promotions.Add(1, 2, 14);
Check(promotions.Tick(15.8) && promotions.Total == 1 && promotions.HighestRank == 2, "later promotions wait without extending current card forever");
Check(!promotions.Tick(21) && promotions.Total == 0, "promotion card expires without stale replay");
promotions.Add(0, 4, 22); promotions.Clear();
Check(!promotions.Tick(24), "session reset clears pending promotions");
promotions.Add(0, 4, 30); Check(!promotions.Tick(1), "clock rollback clears promotion state");
promotions.Add(0, 4, 2); Check(promotions.Tick(3) && promotions.Count(4) == 1, "queue recovers after clock reset");
Check(promotions.Opacity(3) == 0 && promotions.Opacity(4) == 1 && promotions.Opacity(9) == 0, "presentation fade is bounded");
promotions.Clear();
for (int i = 0; i < 20000; i++) promotions.Add(0, 2, 40);
Check(promotions.Tick(41) && promotions.Total == 20000 && promotions.Count(2) == 20000, "mass promotions aggregate without per-unit card allocations");
var hovered = new PromotionQueue();
hovered.Add(0, 1, 100); hovered.Tick(100.7); hovered.Tick(101.7);
Check(hovered.Tick(105.7, true) && hovered.Total == 1 && hovered.Opacity(105.7) == 1,
    "pointer pauses expiry and fade of the visible promotion");
Check(hovered.Tick(108.7) && !hovered.Tick(109.8), "promotion resumes its remaining lifetime after pointer leaves");
hovered.Add(0, 1, 200); hovered.Tick(201);
Check(hovered.Tick(212, true) && hovered.Tick(215.9, true) && !hovered.Tick(216, true),
    "continuous hover adds at most ten seconds rather than keeping the card forever");
hovered.Add(0, 1, 300); hovered.Tick(301); hovered.Add(1, 3, 302);
Check(hovered.Tick(304, true) && hovered.Count(1) == 1 && hovered.Count(3) == 0,
    "new promotion batch remains pending while the visible card is held");
hovered.Tick(315.9, true);
Check(hovered.Tick(316, true) && hovered.Total == 1 && hovered.Count(3) == 1,
    "hover cap releases the old card and displays the waiting batch");
Check(!hovered.Tick(321.1), "a later batch does not inherit the previous card's hover extension");
hovered.Add(0, 1, 400); hovered.Tick(401); hovered.Tick(405, true); hovered.Clear();
hovered.Add(0, 2, 410); hovered.Tick(411);
Check(!hovered.Tick(416.1) && !hovered.HasPending, "session clear removes held time and all prior promotion state");
foreach (double invalidTime in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
{
    hovered.Add(0, 1, 500); hovered.Tick(501); hovered.Tick(502, true);
    Check(!hovered.Tick(invalidTime, true) && hovered.Total == 0 && !hovered.HasPending && hovered.Opacity(503) == 0,
        "invalid hovered presentation time clears the visible card: " + invalidTime);
}
hovered.Add(0, 1, 600); hovered.Tick(601); hovered.Tick(605, true);
Check(!hovered.Tick(604, true) && hovered.Total == 0, "clock rollback after a hover tick clears state even after the original start time");
VeteranText.Configure("Spanish");
Check(VeteranText.PromotedTo("Élite") == "ASCENSO: Élite" && VeteranText.PromotionCount(3) == "3 ASCENSOS", "promotion messages support Spanish");
VeteranText.Configure("English");
Check(VeteranText.PromotedTo("Elite") == "PROMOTED: Elite" && VeteranText.PromotionCount(3) == "3 PROMOTIONS", "promotion messages support English");
Check(Specializations.Damage(Specialization.Assault, 2, true) > .049 && Specializations.Health(Specialization.Assault, 2, true) == 0,
    "Assault unlocks at Veteran with damage only");
Check(Specializations.Health(Specialization.Guardian, 2, true) > .099 && Specializations.Damage(Specialization.Guardian, 2, true) == 0,
    "Guardian unlocks with health only");
Check(Specializations.Experience(Specialization.Mentor, 2, true) == 1.2 && Specializations.Experience(Specialization.Mentor, 1, true) == 1,
    "Mentor rewards earned XP only after unlock");
Check(Specializations.Damage(Specialization.Assault, 4, false) == 0 && Specializations.Health(Specialization.Guardian, 4, false) == 0 &&
    Specializations.Experience(Specialization.Mentor, 4, false) == 1, "disabling specialties suspends every effect");
Check(!Specializations.Valid((Specialization)4) && !Specializations.Valid((Specialization)(-1)) && Specializations.Valid(Specialization.None), "unknown specialties rejected");
var army = new ArmySummary();
Check(Progression.Accumulate(double.MaxValue, 10) == double.MaxValue && Progression.Accumulate(999999999999, 10) == 1e12 &&
    Progression.Accumulate(300, 2.64) == 302.64, "XP growth is capped without reducing extreme existing progression");
army.Add(0, 0, 10, 20, true); army.Add(2, 350, 30, 44, false); army.Add(2, 400, 35, 44, true);
Check(army.Total == 3 && army.Available == 2 && army.Counts[2] == 2 && army.Selectable[2] == 1 && army.Experience[2] == 750 &&
    army.TotalExperience == 750 && army.Health == 75 && army.MaximumHealth == 108, "army summary separates roster and selectable soldiers");
army.Add(5, 10, 1, 1, true); army.Add(0, double.NaN, 1, 1, true); army.Add(0, 10, 0, 1, true);
Check(army.Total == 3, "invalid or dead snapshots do not contaminate the roster");
army.Clear(); Check(army.Total == 0 && army.Counts[2] == 0 && army.Health == 0 && army.Available == 0 && army.Experience[2] == 0,
    "a refreshed roster cannot retain counts from a prior world");
army.Add(4, double.MaxValue, 1, 1, true); army.Add(4, double.MaxValue, 1, 1, true);
Check(Progression.Finite(army.TotalExperience) && Progression.Finite(army.Experience[4]), "extreme valid imported XP cannot overflow army aggregates");
HudLayoutTests.Run(Check);
var timer=Stopwatch.StartNew();
double total=0;
for(int i=0;i<1000000;i++) total += Progression.EffectiveShare(100,0,20,150);
timer.Stop();
Console.WriteLine($"{checks} checks passed. Pure calculation benchmark: 1,000,000 shares in {timer.Elapsed.TotalMilliseconds:F2} ms (not a game FPS benchmark), checksum={total:F2}.");
