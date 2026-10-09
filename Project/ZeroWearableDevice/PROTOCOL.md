# Akka.NET classic remoting, as spoken by a C++ peer

> **Transport note.** The device carries these PDUs over **BLE**, not TCP: chunks tagged
> `0xAB` on the Nordic UART Service, relayed to the host's remoting port by AkkaHost's
> `BleTunnel` (or `pc/ble_akka_bridge.py`). Everything below is unchanged by that - Akka
> needs an ordered, reliable byte stream and BLE is one. The framing, handshake and
> envelope layout are identical whether the bytes arrive over TCP or BLE, which is why the
> device only swapped `akka::IByteStream` implementations.

Everything here was read out of `akkadotnet/akka.net@dev` (the 1.6 line) and then
confirmed against a running `AskBot.Host` with the C++ client in `cpp/`.

Sources: `src/protobuf/WireFormats.proto`, `src/protobuf/ContainerFormats.proto`,
`src/core/Akka.Remote/Transport/AkkaPduCodec.cs`,
`src/core/Akka.Remote/Transport/AkkaProtocolTransport.cs`,
`src/core/Akka.Remote/Transport/DotNetty/DotNettyTransport.cs`,
`src/core/Akka.Remote/EndpointManager.cs`,
`src/core/Akka.Remote/Configuration/Remote.conf`.

## Which transport

Classic remoting (`akka.tcp://`), i.e. the DotNetty TCP transport. The 1.6 branch
also carries an **Artery** transport (`akka://`, Pekko-style, `canonical.port = 25520`)
but it ships `enabled = off` and its own config comment states the two are *not*
wire-compatible. Nothing here applies to Artery.

## Framing

```
[ uint32 length, LITTLE-ENDIAN ][ AkkaProtocolMessage protobuf ]
```

* Length excludes the 4-byte header itself.
* Little-endian because `akka.remote.dot-netty.tcp.byte-order = "little-endian"`
  is the default (`LengthFieldPrepender(Settings.ByteOrder, 4, 0, false)`).
* `maximum-frame-size = 128000b`; a longer length means frame desync, not a big
  message.

## Connection direction: one socket is enough

`akka.remote.use-passive-connections = on` is the default, and
`EndpointManager.AcceptPendingReader` marks an accepted inbound handle as
*writable* when no outbound endpoint to that address exists yet. So the .NET node
replies over the connection the client opened, and **the device needs no
listening socket** — which is the whole reason this is viable on an MCU behind
NAT.

The advertised address still has to be well-formed (`host` and `port` non-empty,
`AkkaPduProtobuffCodec.SerializeAddress` throws otherwise), it just never has to
accept a connection.

## Handshake

```
client --> ASSOCIATE (AkkaHandshakeInfo: origin address + uid)
client <-- ASSOCIATE (the peer's own origin + uid)
both   --> HEARTBEAT every 4s
```

`AkkaProtocolTransport`, `WaitHandshake` state: the inbound side answers an
association attempt with its own `ASSOCIATE`. No cookie, no auth — `cookie = 3`
is marked "not used" in the .proto. `refuseUid` only matters when the peer is
re-associating after a quarantine.

**The trap that costs an afternoon:** the handshake rides the *wrapped*
transport, so `AddressData.protocol` on the wire is `"tcp"`, not `"akka.tcp"` —
`AkkaProtocolTransport` prepends `akka.` on receipt. Send `"akka.tcp"` and the
peer registers you as `akka.akka.tcp://you@host:port`; your `akka.tcp://` sender
paths then match no endpoint, the peer tries to dial your advertised port
instead, that fails, the address gets gated for 5s and every reply goes to dead
letters. Observed exactly once, in this log line:

```
reliableEndpointWriter-akka.akka.tcp%3A%2F%2Faskbot-dev%40127.0.0.1%3A2553-1
```

Actor **paths** are the opposite: those use `akka.tcp://system@host:port/user/x`.

## Failure detector

`akka.remote.transport-failure-detector`: `heartbeat-interval = 4s`,
`acceptable-heartbeat-pause = 120s`. Send a `HEARTBEAT` control PDU every ~4s and
the association survives; go quiet for two minutes and the peer drops it.

## Messages

Every user message is a `payload` PDU whose bytes are an `AckAndEnvelopeContainer`:

```
AkkaProtocolMessage.payload(1) = AckAndEnvelopeContainer {
  ack(1)      = AcknowledgementInfo { cumulativeAck(1) fixed64, nacks(2) repeated fixed64 }
  envelope(2) = RemoteEnvelope {
    recipient(1) = ActorRefData { path(1) string }
    message(2)   = Payload { message(1) bytes, serializerId(2) int32, messageManifest(3) bytes }
    sender(4)    = ActorRefData { path(1) string }
    seq(5)       = fixed64
  }
}
```

`seq` **must always be written** as `ulong.MaxValue` (`kSeqUndefined`). proto3
omits zero defaults, and a missing `seq` reads as system message #0, dragging the
message into the reliable-delivery/resend machinery.

### Serializers worth knowing

From `Akka.Remote/Configuration/Remote.conf`:

| id | serializer | used for |
|---|---|---|
| 1 | `NewtonSoftJsonSerializer` | `System.Object` fallback — payload is UTF-8 JSON text |
| 2 | `ProtobufSerializer` | `Google.Protobuf.IMessage` |
| 16 | `MiscMessageSerializer` | `IActorRef`, `PoisonPill`, `Status.Failure`, ... |
| 17 | `PrimitiveSerializers` | `string` / `int` / `long` |
| 22 | `SystemMessageSerializer` | DeathWatch and friends |

A `string` is therefore: `serializerId = 17`, `messageManifest = "S"`, `message =`
the UTF-8 bytes, nothing else. That is why the C++ side needs no .NET serializer —
and it is verified: `"한글 UTF-8 왕복 테스트"` comes back as 15 chars counted by
.NET. Two legacy manifests (`System.String, System.Private.CoreLib` and
`..., mscorlib`) mean the same thing and are accepted on decode.

### ask, without an ActorSystem

`Ask` sets `sender` to `akka.tcp://<local system>@<local host>:<port>/temp/$N`.
The .NET actor's `Sender.Tell(reply)` then routes back over the passive
connection addressed to that path, and the client matches the `/temp/` suffix to
its pending request. No temp-actor registry, no `ActorSystem` on the C++ side.

The local address used in those sender paths **must** be byte-identical to the
origin sent in the handshake, because that address is the key the peer's endpoint
registry uses.

### Inbound system messages

Anything arriving with `seq != kSeqUndefined` is on the reliable-delivery path.
The client does not implement that path; it replies with a pure-ack container
(`cumulativeAck = seq`) so the peer stops resending, and nothing else. Avoid
`Context.Watch` on the device ref and this stays theoretical.

## What is deliberately not implemented

Cluster membership, DeathWatch, `Akka.Persistence`, custom serializers,
`ActorSelection`, Hyperion/protobuf payloads, TLS. A device that needs any of
those is a device that should be running a real `ActorSystem`.

---

# Application layer: the AskBot chat protocol

The transport above carries one more layer, which is what the device app and the
host actor actually speak. Payloads are **JSON strings** (serializer 17), the same
field names the BLE protocol of `amoled_chat_host` uses, so the device code is a
transport swap rather than a rewrite.

## The client actor

The device registers a local actor and sends *as* it:

```
akka.tcp://askbot-device@<device-ip>:2553/user/chat
```

The host's `ChatActor` therefore sees a `Sender` it can push to at any time -
stages, reply chunks, session changes are plain `Tell`s, not responses to a
request. `RemoteClient::Register("chat", handler)` on the device dispatches
inbound envelopes whose recipient path ends in `/user/chat` to that handler, with
`sender_path` intact.

This is the part that answers "can a small device take part in the actor model":
there is no `ActorSystem` on the ESP32, no mailbox scheduler, no supervision - but
on the wire it is a peer with an address, and the host talks to it as one.

## Device to host (`/user/chat` on the host)

| `t` | JSON | meaning |
|---|---|---|
| `hello` | `{"t":"hello","name":"askbot","fw":"akka-1"}` | sent once per association |
| `text` | `{"t":"text","id":N,"text":"...","tts":false,"out":"off","outLang":"ko","voice":"F1"}` | a question |
| `cancel` | `{"t":"cancel","id":N}` | abandon the running answer |
| `newsession` | `{"t":"newsession","id":N}` | move to a fresh conversation |
| `ping` | `{"t":"ping"}` | liveness check |

## Host to device (`/user/chat` on the device)

| `t` / `st` | JSON | meaning |
|---|---|---|
| `hostinfo` | `{"t":"hostinfo","host":"PC","provider":"netclaw","tts":true,"pcOut":true,"voice":"F1","outLang":"ko","voices":["F1",…],"sttReady":true,"stt":"whisper-small","chat":1,"v":1}` | answer to `hello` |
| `answer` `think` | `{"t":"answer","st":"think","id":N}` | prompt handed to the brain that answers it |
| `answer` `reply` | `{"t":"answer","st":"reply","id":N,"seq":i,"n":k,"text":"...","done":true}` | answer chunk; `seq` 0 starts a fresh answer, concatenate in order |
| `answer` `session` | `{"t":"answer","st":"session","id":N,"n":k}` | conversation number changed |
| `answer` `idle` | `{"t":"answer","st":"idle","id":N}` | nothing running (answer to `cancel`) |
| `answer` `err` | `{"t":"answer","st":"err","id":N,"text":"..."}` | request failed |
| `answer` `pong` | `{"t":"answer","st":"pong","id":N}` | answer to `ping` |

`provider` names whatever answers this device, and the device only displays it: a chat CLI
(`netclaw`, `claude`, `echo`) for the Chat app, or `agent:<model>` when AskBot is served by the
host's own agent. The firmware keeps 24 bytes for the field, so the model's vendor prefix is
dropped (`google/gemma-4-e4b` → `agent:gemma-4-e4b`).

Chunking is UTF-8 safe and defaults to 400 bytes (`Chat.ChunkBytes`), well under
the frame budget; the limit exists because the device appends into a fixed buffer
and redraws per chunk.

Newest-question-wins: a new `text` cancels the request in flight instead of being
refused, matching what the BLE host settled on.

## Spoken answers

When a `text` request carries `"tts":true` and the host has a voice, the answer is
also synthesized and streamed:

| direction | message | meaning |
|---|---|---|
| host -> device | `{"t":"answer","st":"speak","id":N,"fmt":"adpcm","rate":16000,"ch":1,"frames":K,"ms":M}` | an utterance of K frames follows |
| host -> device | .NET `byte[]` (serializer **4**, no manifest) | `0xA6 \| id(1) \| seq(2 LE) \| ADPCM block` |
| host -> device | `{"t":"answer","st":"speak_end","id":N}` | all frames sent; `text` present only on failure |

The device decodes each block into a PSRAM buffer as it arrives and plays the whole
utterance when `speak_end` lands. Two buffers, because the next answer's frames start
arriving while the current one is still playing.

`hostinfo` reports `"tts":true` and the voice id only when the model is actually
installed, so the device hides its voice toggle otherwise - the same degradation the
BLE app applies.

### Where the answer is spoken: `out`

Every `text` and `voice` request also carries `"out"`, the watch's answer mode:

| `out` | the host |
|---|---|
| `"off"` | sends the text only |
| `"watch"` | sends the text, then the `speak` / frames / `speak_end` sequence above |
| `"pc"` | sends the text only, and plays the answer on the **PC's own speakers** at the synthesizer's full rate - no audio crosses the link |

`"tts"` is still sent (`true` exactly when `out` is `"watch"`), so a host that predates
`out` treats PC mode as text only. A host that understands `out` says so with
`"pcOut":true` in `hostinfo`; the device offers PC as a third choice only then, and falls
back to `watch` if it was on PC and reconnects to a host without it. On the device the
mode is the AskBot pill, tapped to cycle off -> watch -> PC. A newer question or a
`cancel` stops PC playback the same way it stops a request.

### Why byte[] and not base64 in JSON

Base64 costs a third more for nothing, and `ByteArraySerializer` (id 4) is a plain
`Serializer` rather than a `SerializerWithStringManifest`, so the manifest field stays
empty and the payload is the frame itself. One ADPCM block per message: 960 samples
(60 ms) in 484 bytes, which keeps the device-side decoder identical to the Chat app's.

### The synthesizer

SuperTonic-3, four ONNX graphs run through `Microsoft.ML.OnnxRuntime`: no Python, no
espeak-ng, no COM. 44.1 kHz float out, resampled on the host to 16 kHz PCM16 with a
windowed-sinc kernel (not NAudio - its resamplers go through Media Foundation, which
would put COM back into a binary that has to survive Native AOT). The model is the one
AgentZeroLite already installed under
`%LOCALAPPDATA%\AgentZeroLite\models\supertonic`; nothing is downloaded.

Measured: ~4 s of CPU for ~20 s of Korean audio at 8 denoising steps, plus ~0.9 s of
one-time model load. It runs inside the Native AOT binary too, which Windows
`System.Speech` cannot.

## Microphone and speech recognition

Speech *input* works from both apps - the Chat app writes its frames to NUS, AskBot sends
them as .NET `byte[]` messages through the tunnel, and the same `ChatActor` handles both:

| direction | message | meaning |
|---|---|---|
| device -> host | `{"t":"voice","id":N,"fmt":"adpcm","rate":16000,"ch":1,"lang":"ko","tts":false,"out":"off"}` | an utterance starts |
| device -> host | `0xA5 \| id(1) \| seq(2 LE) \| ADPCM block` | 60 ms of microphone audio each (960 samples) |
| device -> host | `{"t":"end","id":N}` | utterance finished |
| host -> device | `{"t":"answer","st":"rec","id":N}` | capture accepted |
| host -> device | `{"t":"answer","st":"stt","id":N}` | transcribing (no `text` yet) |
| host -> device | `{"t":"answer","st":"stt","id":N,"text":"…"}` | what the host heard |
| host -> device | `think` / `reply` / `speak` … | the normal answer path takes over |

`hostinfo` advertises `"sttReady":true` and the model name, so a device hides its
microphone when the host cannot transcribe - the same rule as `tts`. It also lists the
voices the host actually has (`voices`), read from `voice_styles/`, so the device's Settings
screen is not a hardcoded list.

## Voice settings: input and output are separate

Three device-side settings travel **with every request** rather than in a settings message,
so a change takes effect on the next question and there is no state to keep in sync:

| field | on | meaning |
|---|---|---|
| `lang` | `voice` | what language to expect. Telling whisper "ko" beats letting it guess, in both accuracy and time |
| `outLang` | `text`, `voice` | what language the answer is spoken in |
| `voice` | `text`, `voice` | which SuperTonic speaker says it |

They are separate because they are separate decisions - asking in Korean and hearing the
answer in English is a reasonable thing to want. The host falls back to its configured
defaults for anything a device does not send.

The model's own spec, read from the files rather than assumed: `tts.json` says
`split: opensource-multilingual`, and each `voice_styles/{id}.json` is a speaker embedding
extracted from a reference WAV with **no language field**. So all 10 voices × all 31
languages are valid combinations. Verified by synthesising the same sentence as F1 and M2 in
Korean (zero-crossing rate 165/s vs 354/s - audibly different speakers) and as F3/M5 in
English.

The device stores the three values in NVS (`voicecfg`) and cycles them from the Settings
screen; `C {"cmd":"voicecfg","in":"ko","out":"en","voice":"M2"}` sets them from the host.

The host decodes each ADPCM block straight into one growing buffer (a gap costs one
block, since each block carries its own predictor), then hands 16 kHz PCM16 to
**whisper.cpp** via Whisper.net. The model is the `ggml-small.bin` AgentZeroLite already
installed; nothing is downloaded.

Two things that decide whether this is usable:

- **Threads.** whisper.cpp with the default thread count took **25.6 s** for a 4.1 s
  capture on this machine. With `Environment.ProcessorCount - 1` (31 here) and the
  language pinned instead of auto-detected, the same capture takes **2.3 s**, and 0.24 s
  once the model is warm.
- **A silence gate.** Left to itself on a quiet capture, whisper invents text - `[구독 /
  좋아요]`, `[감사합니다]`, YouTube boilerplate from its training data - and the watch
  would then ask the LLM about it. A quiet room on this board measures peak -46.8 dBFS,
  rms -58.9 dBFS at the default 30 dB mic gain, so captures below `SilencePeakDb` /
  `SilenceRmsDb` never reach the model and come back as `no speech detected`.

`AkkaHost.exe --talk 4000` asks both apps to record for four seconds as soon as they connect:
the Chat app gets a `C {"cmd":"talk","ms":4000}` line, AskBot gets
`{"t":"cmd","cmd":"talk","ms":4000}` as an actor message. That is how this path gets exercised
without touching the watch.

One microphone, two apps: `device_mic` (in `brookesia_app_claude_hud`) owns the codec handle
and hands it out reference-counted, so each app holds it only while recording. Before that the
Chat app held it forever and AskBot could not record at all.

---

# The Claude HUD channel

The same BLE link carries a third app, and it predates all of this: `hud_ble` consumes two
line tags itself and never passes them to an app hook.

| direction | line | meaning |
|---|---|---|
| host -> device | `S {json}` | Claude Code statusLine: session, model, cost, context %, rate limits |
| host -> device | `E {json}` | Claude Code hook event: `ev`, `tool`, `label`, `host`, `text` |

The payloads are whatever the installed scripts send (`~/.claude/hud_amoled/hud_statusline.ps1`
and `send_event.ps1`); the host validates that the body parses as JSON and forwards it
verbatim. Nothing is queued: the HUD is a display, and a status from ten minutes ago is worth
less than nothing, so lines are dropped while the watch is out of range.

Intake is the contract `ble_bridge.py` defined, kept so that no hook has to be reinstalled:
`POST /status`, `POST /event`, `GET /health` on `127.0.0.1:8765`.
