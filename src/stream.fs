namespace N2O

open System
open System.Text
open System.Threading
open System.Net.WebSockets

// MailboxProcessor-based Tick pusher and pure Async WebSocket looper

module Stream =

    let mutable protocol: Req -> Msg -> Msg = fun _ y -> y

    let sendBytes (ws: WebSocket) ct bytes =
        ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, ct)
        |> Async.AwaitTask

    let sendMsg ws ct (msg: Msg) = async {
        match msg with
        | Text text -> do! sendBytes ws ct (Encoding.UTF8.GetBytes text)
        | Bin arr -> do! sendBytes ws ct arr
        | Nope -> ()
    }

    let telemetry (ws: WebSocket) (inbox: MailboxProcessor<Msg>)
        (ct: CancellationToken) (sup: MailboxProcessor<Sup>) =
        async {
            try
                while not ct.IsCancellationRequested do
                    let! _ = inbox.Receive()
                    do! sendMsg ws ct (Text "TICK")
            finally
                sup.Post(Disconnect <| inbox)

                ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "TELEMETRY", ct)
                |> ignore
        }

    let looper (ws: WebSocket) (req: Req) (bufferSize: int)
        (ct: CancellationToken) (sup: MailboxProcessor<Sup>) =
        async {
            try
                let mutable bytes = Array.create bufferSize (byte 0)
                while not ct.IsCancellationRequested do
                    let! result =
                        ws.ReceiveAsync(ArraySegment<byte>(bytes), ct)
                        |> Async.AwaitTask

                    let recv = bytes.[0..result.Count - 1]

                    match (result.MessageType) with
                    | WebSocketMessageType.Text ->
                        do! protocol req (Text (Encoding.UTF8.GetString recv))
                            |> sendMsg ws ct
                    | WebSocketMessageType.Binary ->
                        do! protocol req (Bin recv)
                            |> sendMsg ws ct
                    | WebSocketMessageType.Close -> ()
                    | _ -> printfn "PROTOCOL VIOLATION"
            finally
                sup.Post(Close <| ws)

                ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "LOOPER", ct)
                |> ignore
        }

