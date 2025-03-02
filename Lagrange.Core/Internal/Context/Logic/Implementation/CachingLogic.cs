using Lagrange.Core.Common.Entity;
using Lagrange.Core.Internal.Context.Attributes;
using Lagrange.Core.Internal.Event;
using Lagrange.Core.Internal.Event.Notify;
using Lagrange.Core.Internal.Event.System;
using Lagrange.Core.Internal.Service;

namespace Lagrange.Core.Internal.Context.Logic.Implementation;

[EventSubscribe(typeof(InfoPushGroupEvent))]
[EventSubscribe(typeof(GroupSysDecreaseEvent))]
[EventSubscribe(typeof(GroupSysIncreaseEvent))]
[BusinessLogic("CachingLogic", "Cache Uin to Uid")]
internal class CachingLogic : LogicBase
{
    private const string Tag = nameof(CachingLogic);
    
    private readonly Dictionary<uint, string> _uinToUid;
    private readonly List<uint> _cachedGroups;
    private readonly List<BotGroup> _cachedGroupEntities;
    
    private readonly List<BotFriend> _cachedFriends;
    private readonly Dictionary<uint, List<BotGroupMember>> _cachedGroupMembers;

    private TaskCompletionSource<List<BotGroup>>? _initCompletionSource;
    
    internal CachingLogic(ContextCollection collection) : base(collection)
    {
        _uinToUid = new Dictionary<uint, string>();
        _cachedGroups = new List<uint>();
        _cachedGroupEntities = new List<BotGroup>();
       
        _cachedFriends = new List<BotFriend>();
        _cachedGroupMembers = new Dictionary<uint, List<BotGroupMember>>();
    }

    public override Task Incoming(ProtocolEvent e)
    {
        switch (e)
        {
            case InfoPushGroupEvent infoPushGroupEvent:
                _cachedGroupEntities.Clear();
                _cachedGroupEntities.AddRange(infoPushGroupEvent.Groups);
                
                if (_initCompletionSource != null)
                {
                    _initCompletionSource.SetResult(_cachedGroupEntities);
                    _initCompletionSource = null;
                }
                
                Collection.Log.LogVerbose(Tag, $"Caching group entities: {infoPushGroupEvent.Groups.Count}");
                break;
            case GroupSysDecreaseEvent groupSysDecreaseEvent:
                return CacheUid(groupSysDecreaseEvent.GroupUin, true);
            case GroupSysIncreaseEvent groupSysIncreaseEvent:
                return CacheUid(groupSysIncreaseEvent.GroupUin, true);
        }

        return Task.CompletedTask;
    }
    
    public Task<List<BotGroup>> GetCachedGroups()
    {
        if (_cachedGroupEntities.Count == 0)
        {
            _initCompletionSource = new TaskCompletionSource<List<BotGroup>>();
            return _initCompletionSource.Task;
        }
        return Task.FromResult(_cachedGroupEntities);
    }

    public async Task<string?> ResolveUid(uint? groupUin, uint friendUin)
    {
        if (_uinToUid.Count == 0) await ResolveFriendsUid();
        if (groupUin == null) return _uinToUid.GetValueOrDefault(friendUin);
        
        await CacheUid(groupUin.Value);

        return _uinToUid.GetValueOrDefault(friendUin);
    }
    
    public async Task<uint?> ResolveUin(uint? groupUin, string friendUid, bool force = false)
    {
        if (_uinToUid.Count == 0) await ResolveFriendsUid();
        if (groupUin == null) return _uinToUid.FirstOrDefault(x => x.Value == friendUid).Key;
        
        await CacheUid(groupUin.Value, force);

        return _uinToUid.FirstOrDefault(x => x.Value == friendUid).Key;
    }
    
    public async Task<List<BotGroupMember>> GetCachedMembers(uint groupUin, bool refreshCache)
    {
        if (!_cachedGroupMembers.TryGetValue(groupUin, out var members) || refreshCache)
        {
            await ResolveMembersUid(groupUin);
            return _cachedGroupMembers.TryGetValue(groupUin, out members) ? members : new List<BotGroupMember>();
        }
        return members;
    }
    
    public async Task<List<BotFriend>> GetCachedFriends(bool refreshCache)
    {
        if (_cachedFriends.Count == 0 || refreshCache) await ResolveFriendsUid();
        return _cachedFriends;
    }

    private async Task CacheUid(uint groupUin, bool force = false)
    {
        if (!_cachedGroups.Contains(groupUin) || force)
        {
            Collection.Log.LogVerbose(Tag, $"Caching group members: {groupUin}");
            await ResolveMembersUid(groupUin);
            _cachedGroups.Add(groupUin);
        }
    }
    
    private async Task ResolveFriendsUid()
    {
        var fetchFriendsEvent = FetchFriendsEvent.Create();
        var events = await Collection.Business.SendEvent(fetchFriendsEvent);
        var friends = events.Count != 0 ? ((FetchFriendsEvent)events[0]).Friends : new List<BotFriend>();
        
        foreach (var friend in friends) _uinToUid.TryAdd(friend.Uin, friend.Uid);
        _cachedFriends.AddRange(friends);
    }

    private async Task ResolveMembersUid(uint groupUin)
    {
        var fetchFriendsEvent = FetchMembersEvent.Create(groupUin);
        var events = await Collection.Business.SendEvent(fetchFriendsEvent);
        
        if (events.Count != 0)
        {
            var @event = (FetchMembersEvent)events[0];
            string? token = @event.Token;

            while (token != null)
            {
                var next = FetchMembersEvent.Create(groupUin, token);
                var results = await Collection.Business.SendEvent(next);
                @event.Members.AddRange(((FetchMembersEvent)results[0]).Members);
                token = ((FetchMembersEvent)results[0]).Token;
            }

            foreach (var member in @event.Members) _uinToUid.TryAdd(member.Uin, member.Uid);
            _cachedGroupMembers[groupUin] = @event.Members;
        }
        else
        {
            _cachedGroupMembers[groupUin] = new List<BotGroupMember>();
        }
    }
}