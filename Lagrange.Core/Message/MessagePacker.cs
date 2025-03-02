using ProtoBuf;
using System.Reflection;
using Lagrange.Core.Message.Entity;
using Lagrange.Core.Utility.Extension;
using Lagrange.Core.Utility.Generator;
using Lagrange.Core.Internal.Packets.Message.C2C;
using Lagrange.Core.Internal.Packets.Message.Component;
using Lagrange.Core.Internal.Packets.Message.Component.Extra;
using Lagrange.Core.Internal.Packets.Message.Element;
using Lagrange.Core.Internal.Packets.Message.Routing;
using ContentHead = Lagrange.Core.Internal.Packets.Message.ContentHead;
using MessageBody = Lagrange.Core.Internal.Packets.Message.MessageBody;
using MessageControl = Lagrange.Core.Internal.Packets.Message.MessageControl;
using PushMsgBody = Lagrange.Core.Internal.Packets.Message.PushMsgBody;
using ResponseHead = Lagrange.Core.Internal.Packets.Message.ResponseHead;
using RoutingHead = Lagrange.Core.Internal.Packets.Message.RoutingHead;

namespace Lagrange.Core.Message;

/// <summary>
/// Pack up message into the Protobuf <see cref="Message"/>
/// </summary>
internal static class MessagePacker
{
    private static readonly Dictionary<Type, List<PropertyInfo>> EntityToElem;
    private static readonly Dictionary<Type, IMessageEntity> Factory;
    
    static MessagePacker()
    {
        EntityToElem = new Dictionary<Type, List<PropertyInfo>>();
        Factory = new Dictionary<Type, IMessageEntity>();

        var assembly = Assembly.GetExecutingAssembly();
        var types = assembly.GetTypeWithMultipleAttributes<MessageElementAttribute>(out var attributeArrays);
        var elemType = typeof(Elem);
        
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            var attributes = attributeArrays[i];

            foreach (var attribute in attributes)
            {
                var property = elemType.GetProperty(attribute.Element.Name);
                if (property != null)
                {
                    if (EntityToElem.TryGetValue(type, out var properties)) properties.Add(property);
                    else EntityToElem[type] = new List<PropertyInfo> { property };
                }
            }

            if (type.CreateInstance() is IMessageEntity factory) Factory[type] = factory;
        }
    }

    public static Internal.Packets.Message.Message Build(MessageChain chain, string selfUid)
    {
        var message = BuildPacketBase(chain);

        foreach (var entity in chain)
        {
            entity.SetSelfUid(selfUid);
            
            if (message.Body != null)
            {
                message.Body.RichText?.Elems.AddRange(entity.PackElement());
                
                if (entity.PackMessageContent() is not { } content) continue;
                if (message.Body.MsgContent is not null) throw new InvalidOperationException("Message content is not null, conflicting with the message entity.");
                    
                using var stream = new MemoryStream();
                Serializer.Serialize(stream, content);
                message.Body.MsgContent = stream.ToArray();
            }
        }

        return message;
    }
    
    public static PushMsgBody BuildFake(MessageChain chain, string selfUid)
    {
        var message = BuildFakePacketBase(chain);

        foreach (var entity in chain)
        {
            entity.SetSelfUid(selfUid);
            
            if (message.Body != null)
            {
                message.Body.RichText?.Elems.AddRange(entity.PackElement());

                if (entity.PackMessageContent() is not { } content) continue;
                if (message.Body.MsgContent is not null) throw new InvalidOperationException("Message content is not null, conflicting with the message entity.");
                    
                using var stream = new MemoryStream();
                Serializer.Serialize(stream, content);
                message.Body.MsgContent = stream.ToArray();
            }
        }

        return message;
    }
    
    public static MessageChain Parse(PushMsgBody message)
    {
        var chain = ParseChain(message);

        if (message.Body?.RichText?.Elems != null) // 怎么Body还能是null的
        {
            foreach (var element in message.Body.RichText.Elems)
            {
                foreach (var (entityType, expectElems) in EntityToElem)
                {
                    foreach (var expectElem in expectElems)
                    {
                        if (expectElem.GetValueByExpr(element) is not null && 
                            Factory[entityType].UnpackElement(element) is { } entity)
                        {
                            chain.Add(entity);
                            break;
                        }
                    }
                }
            }
        }

        return chain;
    }

    public static MessageChain ParsePrivateFile(PushMsgBody message)
    {
        if (message.Body?.MsgContent == null) throw new Exception();
        
        var chain = ParseChain(message);

        var extra = Serializer.Deserialize<FileExtra>(message.Body.MsgContent.AsSpan());
        var file = extra.File;

        if ( file is { FileSize: not null, FileName: not null, FileMd5: not null, FileUuid: not null, FileHash: not null })
        {
            chain.Add(new FileEntity((long)file.FileSize, file.FileName, file.FileMd5, file.FileUuid, file.FileHash));
            return chain;
        }

        throw new Exception();
    }

    private static Internal.Packets.Message.Message BuildPacketBase(MessageChain chain) => new()
    {
        RoutingHead = new RoutingHead
        {
            C2C = chain.IsGroup ? null : new C2C
            {
                Uid = chain.FriendInfo?.Uid,
                Uin = chain.FriendUin
            },
            Grp = !chain.IsGroup ? null : new Grp // for consistency of code so inverted condition
            {
                GroupCode = chain.GroupUin
            },
            Trans0X211 = !chain.HasTypeOf<FileEntity>() ? null : new Trans0X211
            {
                CcCmd = 4,
                Uid = chain.Uid
            }
        },
        ContentHead = new ContentHead
        {
            Type = 1, // regarded as the const
            SubType = 0,
            DivSeq = 0
        },
        Body = new MessageBody { RichText = new RichText { Elems = new List<Elem>() } },
        Seq = (uint)Random.Shared.Next(1000000, 9999999), // 草泥马开摆！
        Rand = (uint)Random.Shared.Next(100000000, int.MaxValue),
        Ctrl = chain.IsGroup ? null : new MessageControl { MsgFlag = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
    };

    private static PushMsgBody BuildFakePacketBase(MessageChain chain) => new()
    {
        ResponseHead = new ResponseHead
        {
            FromUid = chain.SelfUid,
            ToUid = chain.Uid,
            Grp = !chain.IsGroup ? null : new ResponseGrp // for consistency of code so inverted condition
            {
                GroupUin = chain.GroupUin ?? 0,
                MemberName = chain.GroupMemberInfo?.MemberCard ?? ""
            },
            Forward = new ResponseForward
            {
                FriendName = chain.FriendInfo?.Nickname
            }
        },
        ContentHead = new ContentHead
        {
            Type = (uint)(chain.IsGroup ? 82 : 529),
            SubType = chain.IsGroup ? null : 4,
            DivSeq = chain.IsGroup ? null : 4,
            MsgId = (uint)Random.Shared.Next(100000000, int.MaxValue),
            Sequence = (uint?)Random.Shared.Next(1000000, 9999999),
            Timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Field7 = 1,
            Field8 = 0,
            Field9 = 0,
            Forward = new ForwardHead
            {
                Field3 = chain.IsGroup ? null : 2,
                UnknownBase64 = Convert.ToBase64String(ByteGen.GenRandomBytes(32)),
                Avatar = $"https://q1.qlogo.cn/g?b=qq&nk={chain.GroupMemberInfo?.Uin ?? chain.FriendInfo?.Uin}&s=640"
            }
        },
        Body = new MessageBody { RichText = new RichText { Elems = new List<Elem>() } }
    };
    
    private static MessageChain ParseChain(PushMsgBody message)
    {
        var chain = message.ResponseHead.Grp == null
            ? new MessageChain(
                message.ResponseHead.FromUin,
                message.ResponseHead.ToUid ?? string.Empty , 
                message.ResponseHead.FromUid ?? string.Empty, 
                message.ContentHead.Sequence ?? 0,
                message.ContentHead.NewId ?? 0)
            
            : new MessageChain(
                message.ResponseHead.Grp.GroupUin, 
                message.ResponseHead.FromUin, 
                (uint)(message.ContentHead.Sequence ?? 0),
                message.ContentHead.NewId ?? 0);

        chain.Time = DateTimeOffset.FromUnixTimeSeconds(message.ContentHead.Timestamp ?? 0).DateTime;
        
        return chain;
    }
}