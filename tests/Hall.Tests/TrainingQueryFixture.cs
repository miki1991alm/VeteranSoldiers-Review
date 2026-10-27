using System;

// Deliberately small boundary double for the actual production reflection guard.
// These checks exercise invalid contracts and query failures, not Unity job scheduling.
namespace Unity.Entities
{
    public struct EntityQuery
    {
        public Func<bool> Read;
        public bool IsEmptyIgnoreFilter => Read();
    }
}

internal sealed class TrainingFixture
{
    private Unity.Entities.EntityQuery _processTrainersJob;
    internal TrainingFixture(Func<bool> read) => _processTrainersJob = new Unity.Entities.EntityQuery { Read = read };
    // Explicit use keeps the double's private-field warning meaningful and clean.
    internal Unity.Entities.EntityQuery Query => _processTrainersJob;
}
internal sealed class MissingTrainingFixture { }
internal sealed class ChangedTrainingFixture
{
    private int _processTrainersJob = 1;
    internal int Query => _processTrainersJob;
}
internal sealed class StaticTrainingFixture
{
    private static Unity.Entities.EntityQuery _processTrainersJob;
    internal static Unity.Entities.EntityQuery Query { get => _processTrainersJob; set => _processTrainersJob = value; }
}
