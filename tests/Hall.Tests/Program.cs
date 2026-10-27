using System;
using VeteranSoldiers;

int checks = 0;
void Check(bool valid, string description)
{
    if (!valid) throw new Exception("FAIL: " + description);
    checks++;
}
bool Near(double a,double b) => Math.Abs(a-b)<.00001;

// Native training reduces time by 1/(1-modifier). Hall composes the remaining time,
// preserving researched advantages or third-party penalties without additive runaway.
foreach (float research in new[] { -.5f, 0f, .1f, .25f, .5f, .9f })
{
    float combined = HallRules.ComposeTraining(research);
    Check(Near(1-combined,(1-research)*.85),"relative time composes with research " + research);
    Check(combined<1,"finite existing modifier never reaches instant training");
}
Check(float.IsNaN(HallRules.ComposeTraining(float.NaN)),"invalid third-party modifier preserved without arithmetic corruption");
Check(float.IsPositiveInfinity(HallRules.ComposeTraining(float.PositiveInfinity)),"infinite third-party modifier left alone");

double elapsed=HallRules.Advance(0,20,true);
elapsed=HallRules.Advance(elapsed,1000,false); // destruction suspends it, no catch-up on reconstruction.
Check(Near(elapsed,20),"destroyed Hall suspends research");
elapsed=HallRules.Advance(elapsed,39,true);
Check(Near(elapsed,59),"reconstructed Hall resumes stored progress");
elapsed=HallRules.Advance(elapsed,10,true);
Check(Near(elapsed,60),"research completes once at its bound");
Check(Near(HallRules.Advance(23,0,true),23),"pause does not advance research");
Check(Near(HallRules.Advance(23,-50,true),23),"clock rollback cannot reverse research");
Check(Near(HallRules.Advance(23,double.NaN,true),23),"invalid delta rejected");
Check(Near(HallRules.Advance(23,double.PositiveInfinity,true),23),"infinite delta rejected");
Check(HallRules.Mask(HallDoctrine.Tradition)==1 && HallRules.Mask(HallDoctrine.Replacements)==2 && HallRules.Mask(HallDoctrine.Recovery)==4,"unlock bits match schema3");
Check(!HallRules.Valid((HallDoctrine)(-1)) && !HallRules.Valid((HallDoctrine)4),"invalid doctrine identifiers rejected");
Check(HallRules.ExperienceThreshold(HallDoctrine.Tradition)<HallRules.ExperienceThreshold(HallDoctrine.Replacements) &&
    HallRules.ExperienceThreshold(HallDoctrine.Replacements)<HallRules.ExperienceThreshold(HallDoctrine.Recovery),"army milestones increase with doctrines");
foreach (var choice in new[] { HallDoctrine.Tradition, HallDoctrine.Replacements, HallDoctrine.Recovery })
{
    int flag = HallRules.Mask(choice);
    Check(HallRules.Blocker(choice,true,true,HallDoctrine.None,HallDoctrine.None,flag,true)==DoctrineBlocker.None,
        "each unlocked doctrine can be adopted with an operational Hall and resources: " + choice);
    Check(HallRules.Blocker(choice,true,false,HallDoctrine.None,HallDoctrine.None,7,true)==DoctrineBlocker.Hall,
        "earned milestones alone cannot charge for a doctrine without a completed Hall: " + choice);
    Check(HallRules.Blocker(choice,false,true,HallDoctrine.None,HallDoctrine.None,7,true)==DoctrineBlocker.Research,
        "research incompatibility blocks adoption even when the Hall exists: " + choice);
    Check(HallRules.Blocker(choice,true,true,HallDoctrine.None,HallDoctrine.None,0,true)==DoctrineBlocker.Experience,
        "missing combat milestone reports XP rather than an enabled adopt action: " + choice);
    Check(HallRules.Blocker(choice,true,true,HallDoctrine.None,HallDoctrine.None,flag,false)==DoctrineBlocker.Resources,
        "insufficient resources are distinct from the combat milestone: " + choice);
    Check(HallRules.Blocker(choice,true,true,HallDoctrine.None,choice,7,false)==DoctrineBlocker.Active,
        "an active doctrine cannot be charged for twice: " + choice);
    Check(HallRules.Blocker(choice,true,true,HallDoctrine.Recovery,HallDoctrine.None,7,true)==DoctrineBlocker.Preparing,
        "a pending doctrine prevents a second transaction: " + choice);
}
foreach (var invalid in new[] { HallDoctrine.None, (HallDoctrine)(-1), (HallDoctrine)4 })
    Check(HallRules.Blocker(invalid,true,true,HallDoctrine.None,HallDoctrine.None,7,true)==DoctrineBlocker.Invalid,
        "invalid doctrine remains blocked with all other conditions satisfied: " + invalid);
Check(HallRules.Blocker(HallDoctrine.Recovery,true,true,HallDoctrine.None,HallDoctrine.Tradition,7,true)==DoctrineBlocker.None,
    "an active doctrine does not prevent preparing a different unlocked doctrine");
var idle = new HallTrainingIdleGuard(typeof(TrainingFixture));
bool queryEmpty = true;
var trainer = new TrainingFixture(() => queryEmpty);
Check(idle.IsEmpty(trainer),"empty exact native query can skip the modifier transaction");
queryEmpty = false;
Check(!idle.IsEmpty(trainer),"first nonempty update resumes the full modifier transaction");
queryEmpty = true;
Check(idle.IsEmpty(trainer),"completed training returns to the idle shortcut");
Check(!new HallTrainingIdleGuard(typeof(MissingTrainingFixture)).IsEmpty(new MissingTrainingFixture()),"missing native query uses conservative synchronization");
Check(!new HallTrainingIdleGuard(typeof(ChangedTrainingFixture)).IsEmpty(new ChangedTrainingFixture()),"changed field type uses conservative synchronization");
StaticTrainingFixture.Query = trainer.Query;
Check(!new HallTrainingIdleGuard(typeof(StaticTrainingFixture)).IsEmpty(new StaticTrainingFixture()),"a static query cannot be mistaken for the current world's query");
var invalidOwner = new HallTrainingIdleGuard(typeof(TrainingFixture));
Check(!invalidOwner.IsEmpty(new MissingTrainingFixture()),"wrong system instance does not skip native training");
Check(!invalidOwner.IsEmpty(trainer),"failed boundary disables only the optional shortcut");
int failedReads = 0;
var disposed = new TrainingFixture(() => { failedReads++; throw new ObjectDisposedException("native query"); });
var failed = new HallTrainingIdleGuard(typeof(TrainingFixture));
Check(!failed.IsEmpty(disposed),"disposed query falls back without propagating an exception");
Check(!failed.IsEmpty(disposed) && failedReads == 1,"failed query is not retried every frame");
var nullOwner = new HallTrainingIdleGuard(typeof(TrainingFixture));
Check(!nullOwner.IsEmpty(null!),"missing system instance safely retains the transaction");
Console.WriteLine("PASS: " + checks + " Hall rules/boundary checks (no Unity runtime claim).");
