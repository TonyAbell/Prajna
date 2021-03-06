(*---------------------------------------------------------------------------
    Copyright 2013 Microsoft

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.                                                      

    Author: Sanjeev Mehrotra
 ---------------------------------------------------------------------------*)

namespace Prajna.Tools.Network

open System
open System.Net.Sockets
open System.Threading
open System.Diagnostics
open Prajna.Tools
open Prajna.Tools.StringTools
open Prajna.Tools.FSharp
open Prajna.Tools.Queue
open Prajna.Tools.Network
open Prajna.Tools.Process

/// base class which maintains multiple GenericConn connections which
/// - share a common pool of SocketAsyncEventArgs which are processed by a superclass along with 
/// - share a threadpool for receiving and sending
type [<AllowNullLiteral>] GenericNetwork(bStartSendPool, numNetThreads) =
    inherit Network()
    let setFixedLenQ(capacity : int) =
        //fun() -> (new FixedLenQ<SocketAsyncEventArgs>(capacity*3/4, capacity) :> BaseQ<SocketAsyncEventArgs>)
        fun() -> (new FixedLenQ<SocketAsyncEventArgs>(capacity, capacity) :> BaseQ<SocketAsyncEventArgs>)
    let cts = new CancellationTokenSource()

    /// Start with numNetThreads being default
    new (bStartSendPool) =
        new GenericNetwork(bStartSendPool, ThreadPoolWithWaitHandles<string>.DefaultNumParallelExecution)

    /// Allow specification of stackSize, buffer size, bStartSendPool upon construction
    new (stackSize, bufSize, bStartSendPool, recvCbO, sendCbO) as x =
        new GenericNetwork(bStartSendPool)
        then
            match recvCbO with
                | Some(cb) -> x.RecvCb <- cb
                | None -> ()
            match sendCbO with
                | Some(cb) -> x.SendCb <- cb
                | None -> ()
            x.InitStack(stackSize, bufSize, 0)           

    member internal x.CTS with get() = cts.Token

//    member val internal netPool : ThreadPoolWithWaitHandles<string> = null with get
    member val internal netPool = new ThreadPoolWithWaitHandles<string>("network process", numNetThreads) with get
//    member val internal bufProcPool = new ThreadPoolWithWaitHandles<string>("Buf Process") with get
    member internal x.bufProcPool with get() = x.netPool
//    member val internal sendPool =
//        if (bStartSendPool) then
//            new ThreadPoolWithWaitHandles<string>("Send")
//        else
//            null
//        with get
    member internal x.sendPool with get() = x.netPool

    member val internal fnQRecv = setFixedLenQ(24) with get, set
    member val internal fnQSend : Option<unit->BaseQ<SocketAsyncEventArgs>> =
        if (bStartSendPool) then
            Some(setFixedLenQ(24))
        else 
            None
        with get, set
    member val internal onConnect : Option<IConn->obj->unit> = None with get, set
    member val internal onConnectState : obj = null with get, set
    member val internal onSocketClose : Option<IConn->obj->unit> = None with get, set
    member val internal onSocketCloseState : obj = null with get, set

    member val internal bufStackRecv = null with get, set
    member val internal bufStackSend = null with get, set
    member val internal BufStackRecvComp = new Component<obj>() with get
    member val internal BufStackSendComp =
        if (bStartSendPool) then
            new Component<obj>()
        else
            null
        with get

    member val private RecvCb = GenericConn.FinishRecvBuf with get, set
    member val private SendCb = GenericConn.FinishSendBuf with get, set

    member internal x.InitStack(stackSize, bufSize, maxStackSize) =
        x.bufStackRecv <- new SharedStack<SocketAsyncEventArgs>(stackSize,  GenericNetwork.AllocBufRecv bufSize x.RecvCb)
        if (maxStackSize > 0) then
            x.bufStackRecv.MaxStackSize <- maxStackSize
        if (bStartSendPool) then
            x.bufStackSend <- new SharedStack<SocketAsyncEventArgs>(stackSize, GenericNetwork.AllocBufSend bufSize x.SendCb)
        x.BufStackRecvComp.Q <- new GrowQ<obj>()
        x.BufStackRecvComp.Proc <- Component<obj>.ProcessFn
        x.BufStackRecvComp.ConnectTo x.bufProcPool false null None None
        x.BufStackRecvComp.StartProcess x.bufProcPool x.CTS ("GenericConnRecvStackWait") (fun k -> "BufProcPool:"+k)
        if Utils.IsNotNull x.BufStackSendComp then
            x.BufStackSendComp.Q <- new GrowQ<obj>()
            x.BufStackSendComp.Proc <- Component<obj>.ProcessFn
            x.BufStackSendComp.ConnectTo x.sendPool false null None None
            x.BufStackSendComp.StartProcess x.sendPool x.CTS ("GenericConnSendStackWait") (fun k -> "BufProcPool:"+k)
            if (maxStackSize > 0) then
                x.bufStackSend.MaxStackSize <- maxStackSize

    /// Close network connections
    abstract Close : unit->unit
    default x.Close() =
        x.BufStackRecvComp.SelfClose()
        x.BufStackSendComp.SelfClose()
        if (Utils.IsNotNull x.bufProcPool) then
            x.bufProcPool.CloseAllThreadPool()
        if (Utils.IsNotNull x.sendPool) then
            x.sendPool.CloseAllThreadPool()

    static member internal AllocBufRecv (size : int) (cb : SocketAsyncEventArgs->unit) (e : SocketAsyncEventArgs) =
        e.Completed.Add(cb)
        e.SetBuffer(Array.zeroCreate<byte>(size), 0, size)

    static member internal AllocBufSend (size : int) (cb : SocketAsyncEventArgs->unit) (e : SocketAsyncEventArgs) =
        e.Completed.Add(cb)
        e.SetBuffer(Array.zeroCreate<byte>(size), 0, size)

/// A generic connection which processes SocketAsyncEventArgs
and [<AllowNullLiteral>] GenericConn() as x =
    let xSendC = new Component<SocketAsyncEventArgs>()
    let xRecvC = new Component<SocketAsyncEventArgs>()
    let xConn = (x :> IConn)

    let mutable bSocketClosed = false
    let mutable connKey = ""
    let cts = new CancellationTokenSource()

    // for token usage ===============================================
    /// TokenBucket size for sending rate control, tokens in units of bits
    /// see: http://fixunix.com/routers/78968-importance-gigabit-switch-port-buffer-size-question.html
    let mutable bUseSendTokens = false
    let tokensToWaitFor = ref 0L
    let tokenTimer = new Timer(x.TokenWaitCallback, tokensToWaitFor, Timeout.Infinite, Timeout.Infinite)
    let mutable tokens = 0L // in units of bits
    let mutable stopwatch : Stopwatch = null
    let mutable lastTokenUpdateMs = 0L
    let tokenWaitHandle = new ManualResetEvent(false)
    let mutable maxSegmentSize = 0
    let mutable maxTokenSize = 0L
    let mutable sendSpeed = 0L

    // processing of send thread pool ===
    let eSendStackWait = new ManualResetEvent(true)
    let eSendFinished = new ManualResetEvent(false)
    // processing of send without tokens
    let processSendWithoutTokens(e : SocketAsyncEventArgs) =
        e.UserToken <- x
        eSendFinished.Reset() |> ignore
        //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Start send %d bytes" e.Count )
        NetUtils.SendOrClose(xConn, GenericConn.FinishSendBuf, e)
        (true, eSendFinished)
        // sync send - thread pool anyways
//        NetUtils.SyncSendOrClose(xConn, e.Buffer, 0, e.Count)
//        GenericConn.FinishSendBuf(e)
//        (true, null)
    // processing of send with tokens
    let processSendWithTokens(e : SocketAsyncEventArgs) = 
        let length = e.Count
        // add 78 bits for header per MSS, *8 to bits
        let neededTokens = (int64)(length + (length+maxSegmentSize-1)/maxSegmentSize*78)*8L 
        if (Utils.IsNotNull (x.WaitForTokens(neededTokens))) then
            (false, tokenWaitHandle)
        else
            processSendWithoutTokens(e)    

    let mutable eRecvNetwork = Unchecked.defaultof<SocketAsyncEventArgs>

    // for connecting with GenericConn
    // generic recv/send processors for simple commands ====================
    // for read
    let mutable eRecvProcess : SocketAsyncEventArgs = null
    let mutable eRecvOffset = 0
    let mutable eRecvRem = 0
    let mutable curBufRecv : byte[] = null
    let mutable curBufRecvRem = 0
    let mutable curBufRecvOffset = 0
    // for write
    let mutable eSendProcess : SocketAsyncEventArgs = null
    let mutable eSendOffset = 0
    let mutable eSendRem = 0
    let mutable curBufSend : byte[] = null
    let mutable curBufSendRem = 0
    let mutable curBufSendOffset = 0

    /// The internal Component used for sending SocketAsyncEventArgs
    member x.CompSend with get() = xSendC
    /// The internal Component used for receiving SocketAsyncEventArgs
    member x.CompRecv with get() = xRecvC
    /// A connection key to identify the connection
    member x.ConnKey with get() = connKey

    member internal x.SendFinished with get() = eSendFinished
    member internal x.SocketClosed with get() = bSocketClosed
    member internal x.CTS with get() = cts.Token

    // for token usage ===============================================
    // TokenBucket size for sending rate control, tokens in units of bits
    // see: http://fixunix.com/routers/78968-importance-gigabit-switch-port-buffer-size-question.html
    /// Set maximum number of tokens for connection 
    member x.MaxTokenSize with get() = maxTokenSize and set(v) = maxTokenSize <- v
    /// Set maximum sending speed for connection in bits per second
    member x.SendSpeed with get() = sendSpeed and set(v) = sendSpeed <- v

    /// Set token usage - Optional if you wish to implement control of sending rate
    /// <param name="initTokens">The initial number of tokens</param>
    /// <param name="maxTokens">The maximum number of tokens</param>
    /// <param name="initSendSpeed">The sending speed in bits per second</param>
    member x.SetTokenUse(initTokens, maxTokens, mss, initSendSpeed) =
        bUseSendTokens <- true
        stopwatch <- new Stopwatch()
        stopwatch.Start()
        tokens <- initTokens
        maxTokenSize <- maxTokens
        sendSpeed <- initSendSpeed
        maxSegmentSize <- mss

    member private x.ReplenishTokens() =
        let curMs = stopwatch.ElapsedMilliseconds
        let elapsedMs = curMs - lastTokenUpdateMs
        lastTokenUpdateMs <- curMs
        tokens <- tokens + (elapsedMs * sendSpeed / 1000L)
        tokens <- Math.Min(maxTokenSize, tokens)

// repeatedly check for sufficient tokens
//    member x.TokenWaitCallback(o : obj) =
//        let neededTokens = o :?> int64
//        x.ReplenishTokens()
//        let surplusTokens = tokens - neededTokens
//        if (surplusTokens >= 0) then
//            tokenWaitHandle.Set() |> ignore
//        else
//            let waitHandle = x.TokenWait(-surplusTokens)
//            if (Utils.IsNull waitHandle) then // otherwise another callback will occur
//                tokenWaitHandle.Set() |> ignore

    // no more than wait one time
    member private x.TokenWaitCallback(o : obj) =
        x.ReplenishTokens()
        tokenWaitHandle.Set() |> ignore

    member private x.TokenWait(waitForTokens : int64) =
        let waitTimeMs = waitForTokens / sendSpeed
        if (waitTimeMs > 5L) then
            tokenWaitHandle.Reset() |> ignore
            tokensToWaitFor := waitForTokens
            if (tokenTimer.Change(waitTimeMs, int64 Timeout.Infinite)) then
                tokenWaitHandle
            else
                null
        else
            null

    member private x.WaitForTokens(neededTokens : int64) =
        x.ReplenishTokens()
        let surplusTokens = tokens - neededTokens
        if (surplusTokens >= 0L) then
            null
        else
            x.TokenWait(-surplusTokens)

    // connection initialization and IConn interface =======================================
    member val private Net : GenericNetwork = null with get, set

    /// Optional argument - function to call upon socket closing
    member val OnSocketClose : Option<IConn->obj->unit> = None with get, set
    /// State to be passed in when OnSocketClose is called
    member val OnSocketCloseState : obj = null with get, set
    /// Function to call when Send finishes
    member val AfterSendCallback : SocketAsyncEventArgs -> unit = (fun e -> ()) with get, set

    member val private RecvCount = ref 0L with get
    member val private ByteCount = ref 0L with get
    member private x.FinishRecv(e : SocketAsyncEventArgs) =
        Interlocked.Increment(x.RecvCount) |> ignore
        Interlocked.Add(x.ByteCount, int64 e.BytesTransferred) |> ignore
        if (!x.RecvCount % 10000L = 0L) then
            Console.WriteLine("Bytes: {0} Rate: {1} Gbps", !x.ByteCount, float(!x.ByteCount*8L)/stopwatch.Elapsed.TotalSeconds/1.0e9)
        x.Net.bufStackRecv.ReleaseElem(e)
        let (event, e) = x.Net.bufStackRecv.GetElem()
        e.UserToken <- x
        NetUtils.RecvOrClose(x, x.FinishRecv, e)

    /// Start receiving - starts the receiver threadpool which receives SocketAsyncEventArgs
    member val internal SimpleSendRecvTest = 0 with get
    member internal x.StartReceive() =
        if (Utils.IsNull stopwatch) then
            stopwatch <- Stopwatch.StartNew()
        if (x.SimpleSendRecvTest > 0) then
            // if doing simple send/recv test using I/O completion port, x.SimpleSendRecvTest specifies num post per socket
            let eArr = Array.init x.SimpleSendRecvTest (fun _ -> new SocketAsyncEventArgs())
            for i in 0..x.SimpleSendRecvTest-1 do
                //let e = eArr.[i]
                //e.Completed.Add(x.FinishRecv)
                //e.SetBuffer(Array.zeroCreate<byte>(256000), 0, 256000)
                let (event, e) = x.Net.bufStackRecv.GetElem()
                e.UserToken <- x
                NetUtils.RecvOrClose(x, x.FinishRecv, e)
        else
            xRecvC.StartProcess x.Net.bufProcPool x.CTS ("GenericConnRecv:"+connKey) (fun k -> "BufProcPool:"+k)
            // execute receive
            x.NextReceive()

    /// Start sending - starts the seding threadpool which sends SocketAsyncEventArgs
    member internal x.StartSend() =
        // set thread pool functions
        if (bUseSendTokens) then
            xSendC.Proc <- processSendWithTokens
        else
            xSendC.Proc <- processSendWithoutTokens
        xSendC.ConnectTo x.Net.sendPool false null (Some(x.CloseSocketConnection)) (Some(fun _ -> xRecvC.SelfTerminate()))
        xSendC.StartProcess x.Net.sendPool x.CTS ("GenericConnSend:"+connKey) (fun k -> "SendPool:"+k)

    /// Initialize and start connection - called if external code starting connection
    /// <param name="sock">The socket to use</param>
    /// <param name="state">The state representing the GenericNetwork</param>
    /// <param name="start">The code to execute upon start</param>
    member internal x.InitConnectionAndStart(sock : Socket, state : obj) (start : unit->unit) =
        let xg = state :?> GenericNetwork
        x.Net <- xg
        xConn.Socket <- sock
        // create a receiver queue and thread proc
        xRecvC.Q <- xg.fnQRecv()
        // create cancellation token and key for thread pools
        //connKey <- NetUtils.GetIPKey(xConn.Socket)
        connKey <- LocalDNS.GetShowInfo(xConn.Socket.RemoteEndPoint)
        // create a sender thread proc, and add it to the pool      
        match x.Net.fnQSend with
            | Some (fnQ) ->
                xSendC.Q <- fnQ()
                x.StartSend()
            | None -> ()
        // call on connect
        match x.Net.onConnect with
            | None -> ()
            | Some cb -> cb x x.Net.onConnectState
        x.OnSocketClose <- x.Net.onSocketClose
        x.OnSocketCloseState <- x.Net.onSocketCloseState
        start()

    member internal x.InitConnection(sock : Socket, state : obj) =
        x.InitConnectionAndStart (sock, state) x.StartReceive

    // use own CloseSocketDone reference as Socket close would also call this
    member val private CloseSocketDone : int ref = ref -1
    member private x.CloseSocketConnection() =
        if (Interlocked.Increment(x.CloseSocketDone) = 0) then
            Logger.LogF( LogLevel.WildVerbose, (fun _ -> sprintf "SocketClosed Endpoint: %s" connKey))
            bSocketClosed <- true
            if (Utils.IsNotNull xConn.Socket) then
                xConn.Socket.Close()
            match x.OnSocketClose with
                | None -> ()
                | Some cb -> cb x x.OnSocketCloseState
            // following automatically gets called when socket close takes place (see CloseConnection)
            //xRecvC.SelfClose() // go backward 

    // upon socket close
    member private x.CloseConnection() =
        x.CloseSocketConnection()
        // cannot send anymore
        if (Utils.IsNotNull xSendC.Q) then
            xSendC.Q.Clear()
        xSendC.SelfClose()
        xRecvC.SelfClose()

    interface IConn with
        override val Socket = null with get, set
        override x.Init(sock : Socket, state : obj) =
            x.InitConnection(sock, state)
        override x.Close() =
            x.CloseConnection()

    // Send and Recv ===============================================
    // Recv not through threadpool
    member private x.NextReceiveOne() =
        eRecvNetwork <- null
        let event = x.Net.bufStackRecv.GetElem(&eRecvNetwork)
        if (Utils.IsNull event) then
            //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Starting NextReceive" )
            eRecvNetwork.UserToken <- x
            NetUtils.RecvOrClose(x, GenericConn.FinishRecvBuf, eRecvNetwork)
        //else
        //    Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Still waiting" )
        event

    member private x.NextReceive() =
        let event = x.NextReceiveOne()
        if (Utils.IsNotNull event) then
            //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Request Enqueue" )
            x.Net.BufStackRecvComp.Q.EnqueueSync(box(x.NextReceiveOne)) |> ignore

    static member internal FinishRecvBuf(e : SocketAsyncEventArgs) =
        let x = e.UserToken :?> GenericConn
//        x.FinishRecv(e)
// Hongzhi: if socket error is receive timeout, shouldn't close connection?
//        if (e.BytesTransferred <= 0) then 
//            x.Net.bufStackRecv.ReleaseElem(e)
//            Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "%s is disconnected" x.ConnKey )
//            x.CloseConnection()
//        else
            //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Finsihing NextReceive" )
            //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Recv %d bytes from %s" e.BytesTransferred ((x :> IConn).Socket.RemoteEndPoint.ToString()) )
            // sync mode
            //x.RecvQ.EnqueueEventSync(e) |> ignore
        x.ContinueReceive(null, false)

    /// The function to call to enqueue received SocketAsyncEventArgs
    member val RecvQEnqueue = xRecvC.Q.EnqueueWaitTime with get, set

    member private x.ContinueReceive(o : obj, bTimeOut : bool) =
        let (success, event) = x.RecvQEnqueue(eRecvNetwork)
//        // simply dispose of data
//        Interlocked.Increment(x.RecvCount) |> ignore
//        Interlocked.Add(x.ByteCount, int64 eRecvNetwork.BytesTransferred) |> ignore
//        if (!x.RecvCount % 10000L = 0L) then
//            Console.WriteLine("Bytes: {0} Rate: {1} Gbps", !x.ByteCount, float(!x.ByteCount*8L)/stopwatch.Elapsed.TotalSeconds/1.0e9)
//        let (success, event) = (true, null)
//        x.Net.bufStackRecv.ReleaseElem(eRecvNetwork)
        if (success) then
            //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Enqueue %d bytes from %s" eRecvNetwork.BytesTransferred (xConn.Socket.RemoteEndPoint.ToString()) )
            if (x.Net.BufStackRecvComp.Q.IsEmpty) then
                x.NextReceive()
            else
                // enqueue if queue has elements for fairness
                //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Request Enqueue" )
                x.Net.BufStackRecvComp.Q.EnqueueSync(box(x.NextReceiveOne)) |> ignore
        else
            //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "cannot Enqueue %d bytes from %s; TCP Buffer: %d" eRecvNetwork.BytesTransferred (LocalDNS.GetShowInfo(xConn.Socket.RemoteEndPoint)) xConn.Socket.ReceiveBufferSize  )
            BaseQ<_>.Wait(event, x.ContinueReceive, null)

    static member internal FinishSendBuf(e : SocketAsyncEventArgs) =
        let x = e.UserToken :?> GenericConn
        //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Finish send %d bytes to %s" e.Count x.ConnKey )
        x.AfterSendCallback(e) // callback prior to release
        x.Net.bufStackSend.ReleaseElem(e)
        x.CompSend.Item := null // so threadpool picks up next element
        x.SendFinished.Set() |> ignore

    // for connecting with GenericConn ======================
    /// If RecvDequeueGenericConn/x.ProcessRecvGenericConn are being used for component processing
    /// this specifies the current buffer being used to process and copy data from a received SocketAsyncEventArgs
    member x.CurBufRecv with get() = curBufRecv and set(v) = curBufRecv <- v
    /// If RecvDequeueGenericConn/x.ProcessRecvGenericConn are being used for component processing
    /// this specifies the offset within the buffer being to process and copy data from a received SocketAsyncEventArgs
    member x.CurBufRecvOffset with get() = curBufRecvOffset and set(v) = curBufRecvOffset <- v
    /// If RecvDequeueGenericConn/x.ProcessRecvGenericConn are being used for component processing
    /// this specifies the remaining size of the buffer being used to process and copy data from a received SocketAsyncEventArgs
    member x.CurBufRecvRem with get() = curBufRecvRem and set(v) = curBufRecvRem <- v
    // If RecvDequeueGenericConn/x.ProcessRecvGenericConn are being used for component processing
    // this species the offset in the current SocketAsyncEventArgs buffer being processed
    member private x.ERecvOffset with get() = eRecvOffset
    // If RecvDequeueGenericConn/x.ProcessRecvGenericConn are being used for component processing
    // this specifies the remainder in the current SocketAsyncEventArgs buffer being processed
    member private x.ERecvRem with get() = eRecvRem

    /// If ProcessSendGenericConn is being used used for sender component processing
    /// this specifies the current buffer being processed and copied to a SocketAsyncEventArgs
    member x.CurBufSend with get() = curBufSend and set(v) = curBufSend <- v
    /// If ProcessSendGenericConn is being used used for sender component processing
    /// this specifies the offset in the current buffer being processed and copied to a SocketAsyncEventArgs
    member x.CurBufSendOffset with get() = curBufSendOffset and set(v) = curBufSendOffset <- v
    /// If ProcessSendGenericConn is being used used for sender component processing
    /// this specifies the remainder of the current buffer being processed and copied to a SocketAsyncEventArgs
    member x.CurBufSendRem with get() = curBufSendRem and set(v) = curBufSendRem <- v
    // If ProcessSendGenericConn is being used used for sender component processing
    // this specifies the offset within the SocketAsyncEventArgs
    member private x.ESendOffset with get() = eSendOffset
    // If ProcessSendGenericConn is being used used for sender component processing
    // this specifies the remainder within the SocketAsyncEventArgs
    member private x.ESendRem with get() = eSendRem

    /// Generic function for recv dequeue to be used by users of GenericConn class
    /// <param name="dequeueAction">The action used to dequeue a SocketAsyncEventArgs to process</param>
    /// <param nam="e">The SocketAsyncEventArg being processed</param>
    /// This function should be used by using following as example to set the component processors:
    /// x.CompRecv.Dequeue <- x.RecvDequeueGenericConn x.CompRecv.Dequeue
    /// where "x" is an instance of the GenericConn class - that is only dequeueAction should be specified
    member x.RecvDequeueGenericConn (dequeueAction : SocketAsyncEventArgs ref -> bool*ManualResetEvent)
                                    (e : SocketAsyncEventArgs ref) :
                                    bool*ManualResetEvent =
        let (success, event) = dequeueAction(e)
        if (success) then
            eRecvOffset <- 0
            eRecvRem <- (!e).BytesTransferred
        (success, event)

    /// Generic function for SocketAsyncEvent processing to be used by users of GenericConn class
    /// <param name="furtherProcess">
    /// A function to execute when CurBufRecvRem reaches zero
    /// The function should return a ManualResetEvent - if it is non-null, processing cannot continue and receiving Component waits
    /// In this implementation, thread pool is used for the receiver Component processing
    /// </param>    
    /// <param name="e">The SocketAsyncEventArg being currently processed - not set by caller</param>
    /// An example of usage is the following:
    /// x.CompRecv.Proc <- x.ProcessRecvGenericConn ProcessRecvCommand
    /// where "x" is an instance of GenericConn class and ProcessRecvCommand is function to perform further processing
    /// once x.CurBufRecvRem reaches zero
    member x.ProcessRecvGenericConn (furtherProcess : unit->ManualResetEvent) (e: SocketAsyncEventArgs) : bool*ManualResetEvent =
        let mutable event : ManualResetEvent = null
        if (0 = curBufRecvRem) then
            event <- furtherProcess()
        while (eRecvRem > 0 && Utils.IsNull event) do
            Network.SrcDstBlkCopy(e.Buffer, &eRecvOffset, &eRecvRem, curBufRecv, &curBufRecvOffset, &curBufRecvRem)
            if (0 = curBufRecvRem) then
                event <- furtherProcess()
        if (0 = eRecvRem && Utils.IsNull event) then
            x.Net.bufStackRecv.ReleaseElem(e)
            (true, null)
        else
            (false, event)

    member private x.GetNextSendProcess() : ManualResetEvent =
        let event = x.Net.bufStackSend.GetElem(&eSendProcess)
        if (Utils.IsNull event) then
            eSendOffset <- 0
            eSendRem <- eSendProcess.Buffer.Length
            eSendStackWait.Set() |> ignore
            null
        else
            event

    /// A generic processor for copying buffers into SocketAsyncEventArgs for sending on network
    /// <param name="enqueueAction">The action to enqueue the SocketAsyncEventArgs once it is assembled</param>
    /// <param name="furtherProcess">
    /// The function to call once CurBufSendRem reaches zero.
    /// This function should return (bDone, bForceEnqueue) as (bool, bool)
    /// - bDone represents whether we are done proecssing the item to be sent
    /// - bForceEnqueue represents whether we should force the current SocketAsyncEventArgs on the queue or whether we should try
    ///   to coalesce more data from the next item onto the same SocketAsyncEventArgs
    /// </param>
    member x.ProcessSendGenericConn (enqueueAction : SocketAsyncEventArgs->bool*ManualResetEvent)
                                    (furtherProcess : unit->bool*bool) : bool*ManualResetEvent =
        let mutable event : ManualResetEvent = null
        let mutable bDone : bool = false
        let mutable bForceEnqueue : bool = false
        if (0 = eSendRem && (Utils.IsNotNull eSendProcess)) then
            //assert(eSendProcess.Count = eSendProcess.Buffer.Length)
            let output = enqueueAction(eSendProcess)
            let bSuccess = fst output
            event <- snd output
            if (bSuccess) then
                //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Enqueue to %s of size %d" x.ConnKey eSendProcess.Count )
                eSendProcess <- null
            //else
            //    Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Enqueue fails1 to %s" x.ConnKey )
        while (not bDone && Utils.IsNull event) do
            if (null = eSendProcess) then
                if (x.Net.BufStackSendComp.Q.IsEmpty) then
                    event <- x.Net.bufStackSend.GetElem(&eSendProcess)
                    if (Utils.IsNull event) then
                        eSendOffset <- 0
                        eSendRem <- eSendProcess.Buffer.Length
                    else
                        eSendStackWait.Reset() |> ignore
                        event <- eSendStackWait
                        x.Net.BufStackSendComp.Q.EnqueueSync(box(x.GetNextSendProcess)) |> ignore
                else
                    eSendStackWait.Reset() |> ignore
                    event <- eSendStackWait
                    x.Net.BufStackSendComp.Q.EnqueueSync(box(x.GetNextSendProcess)) |> ignore
            if (Utils.IsNull event) then
                while (not bDone && eSendRem > 0) do
                    //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Copy to %s amt: %d" x.ConnKey (Math.Min(curBufSendRem, eSendRem)) )
                    Network.SrcDstBlkCopy(curBufSend, &curBufSendOffset, &curBufSendRem, eSendProcess.Buffer, &eSendOffset, &eSendRem)
                    if (0 = curBufSendRem) then
                        let output = furtherProcess()
                        bDone <- fst output
                        bForceEnqueue <- snd output
                if (0 = eSendRem || bForceEnqueue) then
                    eSendProcess.SetBuffer(0, eSendOffset)
                    let output = enqueueAction(eSendProcess)
                    let bSuccess = fst output
                    event <- snd output
                    if (bSuccess) then
                        //Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Enqueue to %s of size %d" x.ConnKey eSendProcess.Count )
                        eSendProcess <- null
                    //else
                    //    Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Enqueue fails2 to %s" x.ConnKey )
        if (Utils.IsNotNull event) then
            bDone <- false
        (bDone, event)
