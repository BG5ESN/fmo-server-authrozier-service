using System.Text.Json.Serialization;

namespace Sas.Auth;

public sealed class EmqxResponseTemplate : IResponseTemplate
{
    public object BuildAllow(AuthResult result)
    {
        var acl = new List<EmqxAclEntry>();
        foreach (var topic in result.AllTopics)
            acl.Add(new EmqxAclEntry { Permission = "allow", Action = "all", Topic = topic });
        foreach (var topic in result.PubTopics)
            acl.Add(new EmqxAclEntry { Permission = "allow", Action = "publish", Topic = topic });
        foreach (var topic in result.SubTopics)
            acl.Add(new EmqxAclEntry { Permission = "allow", Action = "subscribe", Topic = topic });

        return new EmqxAllowResponse
        {
            Result = "allow",
            IsSuperuser = result.IsSuperuser,
            ClientAttrs = new EmqxClientAttrs
            {
                Callsign = result.Callsign,
                Uid = result.Uid.ToString()
            },
            ExpireAt = result.ExpireAt,
            Acl = acl
        };
    }

    public object BuildDeny()
    {
        return new { result = "deny" };
    }

    private sealed class EmqxAllowResponse
    {
        [JsonPropertyName("result")]
        public string Result { get; init; } = "allow";

        [JsonPropertyName("is_superuser")]
        public bool IsSuperuser { get; init; }

        [JsonPropertyName("client_attrs")]
        public EmqxClientAttrs ClientAttrs { get; init; } = new();

        [JsonPropertyName("expire_at")]
        public long ExpireAt { get; init; }

        [JsonPropertyName("acl")]
        public List<EmqxAclEntry> Acl { get; init; } = [];
    }

    private sealed class EmqxClientAttrs
    {
        [JsonPropertyName("callsign")]
        public string Callsign { get; init; } = "";

        [JsonPropertyName("uid")]
        public string Uid { get; init; } = "";
    }

    private sealed class EmqxAclEntry
    {
        [JsonPropertyName("permission")]
        public string Permission { get; init; } = "allow";

        [JsonPropertyName("action")]
        public string Action { get; init; } = "all";

        [JsonPropertyName("topic")]
        public string Topic { get; init; } = "";
    }
}
