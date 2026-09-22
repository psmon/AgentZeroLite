namespace AgentOne.Tests;

/// <summary>
/// AGENT_ONE_HOME is a process-wide environment variable, so two test classes
/// that each point it at their own temp directory will trample each other when
/// xUnit runs them in parallel — one class's ConfigStore.Load() lands in the
/// other's home. Every class that relocates the home joins this collection so
/// they run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public class AgentOneHomeCollection
{
    public const string Name = "agent-one home";
}
