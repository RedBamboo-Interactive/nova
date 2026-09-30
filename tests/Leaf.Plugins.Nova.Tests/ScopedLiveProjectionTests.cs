using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;
using static Leaf.Plugins.Nova.Tests.CallbackDeliveryTests;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ScopedLiveProjectionTests
{
    [Fact] public async Task ExactOwnerAgentConfidentialTargetPersistsOnceAcrossConcurrentRetries()
    {
        using var f=new Fixture();
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>f.Live.PostProjectionAsync(f.Projection)));
        var message=Assert.Single(f.Messages);Assert.Equal(f.Target.Id,message.DiscussionId);
        Assert.Equal("system",message.Role);Assert.Equal(f.Projection.Content,message.Content);
        var invalidation=Assert.Single(f.Events);Assert.Equal("discussion.changed",invalidation.type);
        Assert.DoesNotContain("content",invalidation.data.ToJsonString());
        Assert.DoesNotContain("private-summary",invalidation.data.ToJsonString());
        Assert.DoesNotContain("evidence",invalidation.data.ToJsonString());
    }
    [Theory]
    [InlineData("wrong-user")][InlineData("wrong-agent")][InlineData("unknown")]
    [InlineData("closed")][InlineData("archiving")][InlineData("ambiguous")]
    [InlineData("public")][InlineData("local-owner")][InlineData("invalid-owner")]
    [InlineData("invalid-agent")][InlineData("unknown-policy")][InlineData("no-key")]
    [InlineData("overflow")]
    public async Task RejectedTargetsNeverFallbackToCanonicalGlobalLive(string reason)
    {
        using var f=new Fixture();var p=f.Projection;
        switch(reason)
        {
            case "wrong-user":p=p with {Recipient=p.Recipient! with {OwnerUserId=Guid.NewGuid().ToString()}};break;
            case "wrong-agent":p=p with {Recipient=p.Recipient! with {AgentId=Guid.NewGuid().ToString()}};break;
            case "unknown":f.Entities.Remove(f.Target);break;
            case "closed":f.Target.Data["status"]="archived";break;
            case "archiving":f.Target.Data["status"]="archiving";break;
            case "ambiguous":f.Entities.Add(f.Discussion("duplicate",f.Owner,f.Agent,true));break;
            case "public":f.Target.Data["confidential"]=false;break;
            case "local-owner":f.Target.Data["owner_id"]="local-user";break;
            case "invalid-owner":p=p with {Recipient=p.Recipient! with {OwnerUserId="local-user"}};break;
            case "invalid-agent":p=p with {Recipient=p.Recipient! with {AgentId="nova"}};break;
            case "unknown-policy":p=p with {Recipient=p.Recipient! with {Disclosure=(PluginLiveDisclosure)99}};break;
            case "no-key":p=p with {IdempotencyKey=null};break;
            case "overflow":for(var i=0;i<513;i++){var closed=f.Discussion("other"+i,f.Owner,f.Agent,true);closed.Data["status"]="archived";f.Entities.Add(closed);}break;
        }
        await f.Live.PostProjectionAsync(p);Assert.Empty(f.Messages);Assert.Empty(f.Events);
    }
    [Theory][InlineData("close")][InlineData("owner")][InlineData("agent")][InlineData("public")][InlineData("deleted")][InlineData("ambiguous")]
    public async Task AuthoritativeAdmissionRechecksTargetInsideExistingWriteGate(string race)
    {
        using var f=new Fixture();f.BeforeEntityRead=()=>
        {
            switch(race){case "close":f.Target.Data["status"]="archived";break;
                case "owner":f.Target.Data["owner_id"]=Guid.NewGuid().ToString();break;
                case "agent":f.Target.Data["agent"]=Guid.NewGuid().ToString();break;
                case "public":f.Target.Data["confidential"]=false;break;case "deleted":f.Entities.Remove(f.Target);break;
                case "ambiguous":f.Entities.Add(f.Discussion("new-live",f.Owner,f.Agent,true));break;}
        };
        await f.Live.PostProjectionAsync(f.Projection);Assert.Empty(f.Messages);Assert.Empty(f.Events);
    }
    [Fact] public async Task NonConfidentialSummaryRequiresExplicitDisclosureAndLegacyPresencePathIsUnchanged()
    {
        using var f=new Fixture();f.Target.Data["confidential"]=false;
        await f.Live.PostProjectionAsync(f.Projection with {Recipient=f.Projection.Recipient! with {Disclosure=PluginLiveDisclosure.OwnerApprovedSummary}});
        Assert.Equal(f.Target.Id,Assert.Single(f.Messages).DiscussionId);
        f.Messages.Clear();f.Events.Clear();
        await f.Live.PostProjectionAsync(new("presence","legacy-presence",IdempotencyKey:"legacy"));
        Assert.Equal(f.Global.Id,Assert.Single(f.Messages).DiscussionId);
        Assert.Equal("discussion.event",Assert.Single(f.Events).type);
    }
    private sealed class Fixture:IDisposable
    {
        public string Owner=Guid.NewGuid().ToString(),Agent=Guid.NewGuid().ToString();
        public List<LeafEntity> Entities=[];public List<DiscussionMessage> Messages=[];
        public List<(string type,JsonObject data)> Events=[];public Action? BeforeEntityRead;
        public LeafEntity Target,Global;public LiveEvents Live;private AgentDirectory directory;
        public PluginLiveEventProjection Projection=>new("test-contributor","private-summary",new(){["evidence"]="private-link"},"one-logical-event")
            {Recipient=new(Owner,Agent)};
        public Fixture()
        {
            Target=Discussion("target",Owner,Agent,true);Global=Discussion("global",Guid.NewGuid().ToString(),"canonical-agent",false);
            Entities.AddRange([Target,Global]);
            var store=Proxy.Create<IEntityStore>((method,args)=>method switch
            {
                "QueryAsync"=>Query((EntityQuery)args[0]!),
                "GetBySlugAsync"=>Task.FromResult(Entities.FirstOrDefault(e=>e.Slug==(string)args[0]!)),
                "GetAsync"=>ReadEntity((Guid)args[0]!),_=>throw new NotSupportedException(method)
            });
            var discussions=Proxy.Create<IDiscussions>((method,args)=>method switch
            {
                "GetMessagesAsync"=>ReadMessages((Guid)args[0]!),"PostAsync"=>Post(args),_=>throw new NotSupportedException(method)
            });
            var events=Proxy.Create<IPluginEvents>((method,args)=>
            {
                if(method=="Subscribe")return new Noop();
                if(method=="PublishAsync"){Events.Add(((string)args[0]!, (JsonObject)args[1]!));return Task.CompletedTask;}
                throw new NotSupportedException(method);
            });
            directory=new(store,events){NovaAgentId="canonical-agent"};
            var client=new RedComputeClient(Proxy.Create<IComputeGateway>((_,_)=>throw new Exception("System LIVE must not invoke Compute")));
            var ds=new DiscussionStore(store,discussions);
            Live=new(ds,new EventInjector(discussions,events,client,directory,store,new ConversationUnread(ds,discussions,client)),directory);
        }
        private Task<LeafEntity?> ReadEntity(Guid id){BeforeEntityRead?.Invoke();return Task.FromResult(Entities.FirstOrDefault(e=>e.Id==id));}
        private Task<IReadOnlyList<LeafEntity>> Query(EntityQuery q)=>Task.FromResult<IReadOnlyList<LeafEntity>>(Entities
            .Where(e=>q.DataEquals is null || q.DataEquals.All(p=>e.Data[p.Key]?.ToString()==p.Value?.ToString())).Skip(q.Offset).Take(q.Limit).ToArray());
        private async Task<IReadOnlyList<DiscussionMessage>> ReadMessages(Guid id){await Task.Yield();return Messages.Where(m=>m.DiscussionId==id).ToArray();}
        private Task Post(object?[] a){Messages.Add(new(Messages.Count+1,(Guid)a[0]!, (string)a[1]!, (string)a[2]!, (JsonObject)a[3]!,DateTimeOffset.UtcNow));return Task.CompletedTask;}
        public LeafEntity Discussion(string id,string owner,string agent,bool confidential)=>new(Guid.NewGuid(),"discussion",DiscussionStore.Slug(id),id,
            new(){["app"]="nova",["discussion_id"]=id,["type"]="live",["owner_id"]=owner,["agent"]=agent,["confidential"]=confidential,["status"]="idle"},DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"test");
        public void Dispose()=>directory.Dispose();private sealed class Noop:IDisposable{public void Dispose(){}}
    }
}
