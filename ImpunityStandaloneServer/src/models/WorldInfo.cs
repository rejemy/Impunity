#nullable enable

using System.Collections.Generic;

namespace Impunity.StandaloneServer;


public class WorldInfo
{
	public string? WorldId { get; set; }
	public string? WorldName { get; set; }
	public int CurrentPlayers { get; set; }
	public int MaxPlayers { get; set; }

}

public class WorldsInfo
{
	public List<WorldInfo>? Worlds { get; set; }
}