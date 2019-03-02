using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Common;
using AElf.Kernel;
using AElf.Kernel.Account.Application;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.TransactionPool.Infrastructure;
using AElf.OS.Network.Events;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.EventBus.Local;

namespace AElf.OS.Network.Grpc
{
    /// <summary>
    /// Implementation of the grpc generated service. It contains the rpc methods
    /// exposed to peers.
    /// </summary>
    public class GrpcServerService : PeerService.PeerServiceBase
    {
        private readonly ChainOptions _chainOptions;
        
        private readonly IPeerPool _peerPool;
        private readonly IBlockchainService _blockChainService;
        
        public ILocalEventBus EventBus { get; set; }

        public ILogger<GrpcServerService> Logger { get; set; }
        
        private int ChainId => _chainOptions.ChainId;

        public GrpcServerService(IOptionsSnapshot<ChainOptions> options, IPeerPool peerPool, IBlockchainService blockChainService)
        {
            _peerPool = peerPool;
            _blockChainService = blockChainService;
            
            EventBus = NullLocalEventBus.Instance;
            Logger = NullLogger<GrpcServerService>.Instance;

            _chainOptions = options.Value;
        }

        /// <summary>
        /// First step of the connect/auth process. Used to initiate a connection. The provided payload should be the
        /// clients authentication information. When receiving this call, protocol dictates you send the client your auth
        /// information. The response says whether or not you can connect.
        /// </summary>
        public override async Task<AuthResponse> Connect(Handshake handshake, ServerCallContext context)
        {
            Logger.LogTrace($"{context.Peer} has initiated a connection request.");

            try
            {                
                var peer = GrpcUrl.Parse(context.Peer);
                var peerAddress = peer.IpAddress + ":" + handshake.HskData.ListeningPort;
                
                Logger.LogDebug($"Attempting to create channel to {peerAddress}");
                
                Channel channel = new Channel(peerAddress, ChannelCredentials.Insecure);
                var client = new PeerService.PeerServiceClient(channel);

                if (channel.State != ChannelState.Ready)
                {
                    var c = channel.WaitForStateChangedAsync(channel.State);
                }

                var grpcPeer = new GrpcPeer(channel, client, handshake.HskData, peerAddress, peer.ToIpPortFormat());
                
                // Verify auth
                bool valid = _peerPool.AuthenticatePeer(peerAddress, handshake);

                if (!valid)
                    return new AuthResponse { Err = AuthError.WrongAuth };
                
                // send our credentials
                var hsk = await _peerPool.GetHandshakeAsync();
                var resp = client.Authentify(hsk);
                
                // If auth ok -> add it to our peers
                _peerPool.AddPeer(grpcPeer);
                
                return new AuthResponse { Success = true, Port = resp.Port };
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Error during connect, peer: {context.Peer}.");
                return new AuthResponse { Err = AuthError.UnknownError };
            }
        }

        /// <summary>
        /// Second step of the connect/auth process. This takes place after the connect to receive the peers
        /// information and on return let him know that we've validated.
        /// </summary>
        public override Task<AuthResponse> Authentify(Handshake request, ServerCallContext context)
        {
            var peer = GrpcUrl.Parse(context.Peer);
            return Task.FromResult(new AuthResponse { Success = true, Port = peer.ToIpPortFormat() });
        }

        /// <summary>
        /// This method is called when another peer broadcasts a transaction.
        /// </summary>
        public override async Task<VoidReply> SendTransaction(Transaction tx, ServerCallContext context)
        {
            try
            {
                await EventBus.PublishAsync(new TransactionsReceivedEvent()
                {
                    Transactions = new List<Transaction>() {tx}
                });
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error during connect, peer: {context.Peer}.");
            }
            
            return new VoidReply();
        }

        /// <summary>
        /// This method is called when a peer wants to broadcast an announcement.
        /// </summary>
        public override async Task<VoidReply> Announce(PeerNewBlockAnnouncement an, ServerCallContext context)
        {
            if (an?.BlockHash == null)
            {
                Logger.LogError($"Received null announcement or header from {context.Peer}.");
                return new VoidReply();
            }
                
            try
            {
                Logger.LogDebug($"Received announce {an.BlockHash} from {context.Peer}.");
                await EventBus.PublishAsync(new AnnoucementReceivedEventData(ChainId, an, GrpcUrl.Parse(context.Peer).ToIpPortFormat()));
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Error during announcement processing, peer: {context.Peer}.");
            }
            
            return new VoidReply();
        }

        /// <summary>
        /// This method returns a block. The parameter is a <see cref="BlockRequest"/> object, if the value
        /// of <see cref="BlockRequest.Id"/> is not null, the request is by ID, otherwise it will be
        /// by height.
        /// </summary>
        public override async Task<BlockReply> RequestBlock(BlockRequest request, ServerCallContext context)
        {
            if (request == null)
                return new BlockReply();
            
            try
            {
                Block block;
                if (request.Id != null && request.Id.Length > 0)
                {
                    Logger.LogDebug($"Peer {context.Peer} requested block with id {request.Id.ToByteArray().ToHex()}.");
                    block = await _blockChainService.GetBlockByHashAsync( Hash.LoadByteArray(request.Id.ToByteArray()));
                }
                else
                {
                    Logger.LogDebug($"Peer {context.Peer} requested block at height {request.Height}.");
                    block = await _blockChainService.GetBlockByHeightAsync(request.Height);
                }
                
                Logger.LogDebug($"Sending {block} to {context.Peer}.");
                
                return new BlockReply { Block = block };
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Error during block request handle, peer: {context.Peer}.");
            }
            
            return new BlockReply();
        }

        public override async Task<BlockList> RequestBlocks(BlocksRequest request, ServerCallContext context)
        {
            if (request == null)
                return new BlockList();

            try
            {
                var blocks = await _blockChainService.GetBlocksAsync(ChainId, request.FirstBlockId, request.Count);
                
                BlockList blockList = new BlockList();

                if (blocks == null)
                    return blockList;

                blockList.Blocks.AddRange(blocks);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error during RequestBlock handle.");
            }
            
            return new BlockList();
        }

        public override async Task<BlockIdList> RequestBlockIds(BlockIdsRequest request, ServerCallContext context)
        {
            if (request == null)
                return new BlockIdList();

            if (request.FirstBlockId == ByteString.Empty)
            {
                Logger.LogError($"Request ids first block hash is null from {context.Peer}.");
                return new BlockIdList();
            }
            
            if (request.Count <= 0)
            {
                Logger.LogError($"Request ids count is invalid from {context.Peer}.");
                return new BlockIdList();
            }
            
            try
            {
                Logger.LogDebug($"Peer {context.Peer} requested block ids: from {Hash.LoadByteArray(request.FirstBlockId.ToByteArray())}, count : {request.Count}.");
                
                var headers = await _blockChainService.GetReversedBlockHashes(
                    Hash.LoadByteArray(request.FirstBlockId.ToByteArray()), request.Count);
                
                BlockIdList list = new BlockIdList();
                
                if (headers == null || headers.Count <= 0)
                    return list;
                
                list.Ids.AddRange(headers.Select(h => ByteString.CopyFrom(h.DumpByteArray())).ToList());

                return list;
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error during RequestBlock handle.");
            }
            
            return new BlockIdList();
        }

        /// <summary>
        /// Clients should call this method to disconnect explicitly.
        /// </summary>
        public override Task<VoidReply> Disconnect(DisconnectReason request, ServerCallContext context)
        {
            try
            {
                _peerPool.ProcessDisconnection(GrpcUrl.Parse(context.Peer).ToIpPortFormat());
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error during Disconnect handle.");
            }
            
            return Task.FromResult(new VoidReply());
        }
    }
}