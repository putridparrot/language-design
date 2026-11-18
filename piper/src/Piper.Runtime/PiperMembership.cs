namespace Piper.Runtime;

/// <summary>
/// Where a cluster's nodes find each other: the provider a cluster (Piper.Actors.Remote's <c>Cluster</c>) asks for its
/// members, chosen by configuration (<c>Cluster:Provider</c>): <c>memory</c>, the local one; <c>static</c>, a list of
/// seed nodes; a database's (<c>sqlite</c>, <c>sqlserver</c>, <c>postgres</c>, with the data plugin), whose nodes hold
/// leases in a table they share; or one another plugin registers. It needn't be exact: the nodes ping each other, so a
/// member it lists that's down is found to be, and a node it doesn't list is learned from the nodes that do know it.
/// </summary>
/// <remarks>
/// A cluster calls <see cref="JoinAsync"/> as its node joins and again every round (a ping of the members, each
/// heartbeat) while it's a member, and <see cref="MembersAsync"/> every round too, so a provider that keeps leases
/// renews them there. Either may do the work less often than it's called, as a database's does.
/// </remarks>
public interface IClusterMembership
{
    /// <summary>Records that the node at <paramref name="address"/> is (still) a member of <paramref name="cluster"/>.</summary>
    Task JoinAsync(string cluster, string address);

    /// <summary>The nodes that are, or may be, members of <paramref name="cluster"/>.</summary>
    Task<IReadOnlyList<string>> MembersAsync(string cluster);

    /// <summary>Records that the node at <paramref name="address"/> has left <paramref name="cluster"/>.</summary>
    Task LeaveAsync(string cluster, string address);
}
