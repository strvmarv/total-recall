module TotalRecall.Core.SyncStamp

// Timestamps, machine ids, stamps and the hybrid logical clock for git sync
// (milestone 0b-1). Pure: no I/O, no mutable state. The wire forms are fixed by
// docs/superpowers/specs/2026-11-15-git-sync-record-format-design.md.

open System

/// Largest counter a stamp can carry (six decimal digits).
[<Literal>]
let maxCounter = 999_999

/// Largest instant the text form can express: 9999-12-31T23:59:59.999Z.
[<Literal>]
let maxTimeMs = 253_402_300_799_999L

/// A stamp or clock more than this far ahead of the wall clock is "far future".
[<Literal>]
let driftLimitMs = 86_400_000L

let private isDigit (c: char) = c >= '0' && c <= '9'

let private isLowerHex (c: char) = isDigit c || (c >= 'a' && c <= 'f')

/// Parses `len` ASCII digits starting at `start`; the caller has already checked the digits.
let private digits (s: string) (start: int) (len: int) : int =
    let mutable v = 0
    for i in start .. start + len - 1 do
        v <- v * 10 + int s.[i] - int '0'
    v

let private allDigits (s: string) (start: int) (len: int) : bool =
    let mutable ok = true
    for i in start .. start + len - 1 do
        if not (isDigit s.[i]) then ok <- false
    ok

let private epochTicks = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks

/// `yyyy-MM-ddTHH:mm:ss.fffZ`, UTC, the only timestamp form on the wire.
module Timestamp =

    /// 0001-01-01T00:00:00.000Z
    let minMs = -62_135_596_800_000L

    let maxMs = maxTimeMs

    let inRange (ms: int64) : bool = ms >= minMs && ms <= maxMs

    /// Precondition: `inRange ms`; otherwise throws `ArgumentOutOfRangeException`.
    let format (ms: int64) : string =
        if not (inRange ms) then raise (ArgumentOutOfRangeException(nameof ms))
        let dt = DateTime(epochTicks + ms * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc)
        let inv = Globalization.CultureInfo.InvariantCulture
        dt.Year.ToString("D4", inv) + "-" + dt.Month.ToString("D2", inv) + "-" + dt.Day.ToString("D2", inv)
        + "T" + dt.Hour.ToString("D2", inv) + ":" + dt.Minute.ToString("D2", inv) + ":" + dt.Second.ToString("D2", inv)
        + "." + dt.Millisecond.ToString("D3", inv) + "Z"

    /// Strict inverse of `format`: ASCII digits in fixed positions, a real calendar date and time.
    let tryParse (s: string) : int64 option =
        if isNull s || s.Length <> 24 then None
        elif s.[4] <> '-' || s.[7] <> '-' || s.[10] <> 'T' || s.[13] <> ':' || s.[16] <> ':' || s.[19] <> '.' || s.[23] <> 'Z' then None
        elif not (allDigits s 0 4 && allDigits s 5 2 && allDigits s 8 2 && allDigits s 11 2 && allDigits s 14 2 && allDigits s 17 2 && allDigits s 20 3) then None
        else
            let y, mo, d = digits s 0 4, digits s 5 2, digits s 8 2
            let h, mi, sec, ms = digits s 11 2, digits s 14 2, digits s 17 2, digits s 20 3
            if y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59 then None
            else
                let ticks = DateTime(y, mo, d, h, mi, sec, DateTimeKind.Utc).Ticks
                Some ((ticks - epochTicks) / TimeSpan.TicksPerMillisecond + int64 ms)

/// A machine's identity inside a stamp: exactly eight lower-case hex characters.
type MachineId = private MachineId of string

module MachineId =

    let tryCreate (s: string) : MachineId option =
        if not (isNull s) && s.Length = 8 && String.forall isLowerHex s then Some (MachineId s) else None

    let value (MachineId s) : string = s

/// One change's position in time. Field order gives the ordering: time, counter, machine.
type Stamp = { TimeMs: int64; Counter: int; Machine: MachineId }

module Stamp =

    /// True when the stamp can be written and read back.
    let inDomain (s: Stamp) : bool =
        s.TimeMs >= 0L && s.TimeMs <= maxTimeMs && s.Counter >= 0 && s.Counter <= maxCounter

    /// `<timestamp>.<6-digit counter>.<machine>`. Precondition: `inDomain s`.
    let format (s: Stamp) : string =
        Timestamp.format s.TimeMs + "." + s.Counter.ToString("D6", Globalization.CultureInfo.InvariantCulture) + "." + MachineId.value s.Machine

    /// Strict inverse of `format`; `None` for anything else, including a time before 1970.
    let tryParse (s: string) : Stamp option =
        if isNull s || s.Length <> 40 || s.[24] <> '.' || s.[31] <> '.' || not (allDigits s 25 6) then None
        else
            match Timestamp.tryParse (s.Substring(0, 24)), MachineId.tryCreate (s.Substring(32, 8)) with
            | Some t, Some m when t >= 0L -> Some { TimeMs = t; Counter = digits s 25 6; Machine = m }
            | _ -> None

    let compare (a: Stamp) (b: Stamp) : int = Operators.compare a b

/// The machine's persisted clock: the largest (time, counter) it has issued or observed.
type Clock = { ClockMs: int64; ClockCounter: int }

type ClockError =
    /// The next stamp would pass the last time the text form can express.
    | Exhausted

/// `Clock` is the clock to persist; `FarFuture` says the stamp is more than `driftLimitMs` ahead of the wall clock.
type IssueResult = { Stamp: Stamp; Clock: Clock option; FarFuture: bool }

type ObserveResult = { Clock: Clock option; FarFuture: bool }

type GuardResult = { Clock: Clock option; Reset: bool }

module Clock =

    let private clampNow (nowMs: int64) : int64 = max 0L (min maxTimeMs nowMs)

    /// Issues a stamp strictly greater than the clock and than `baseStamp` (the stamp the changed
    /// field carried before). A stamp more than `driftLimitMs` ahead of `nowMs` is returned for
    /// ordering but the clock is not moved to it.
    let issue
        (machine: MachineId)
        (nowMs: int64)
        (clock: Clock option)
        (baseStamp: Stamp option)
        : Result<IssueResult, ClockError> =
        let now = clampNow nowMs
        let t =
            [ Some now
              clock |> Option.map (fun c -> c.ClockMs)
              baseStamp |> Option.map (fun b -> b.TimeMs) ]
            |> List.choose id
            |> List.max
        let counters =
            [ match clock with
              | Some c when c.ClockMs = t -> yield c.ClockCounter
              | _ -> ()
              match baseStamp with
              | Some b when b.TimeMs = t -> yield b.Counter
              | _ -> () ]
        let c0 = match counters with [] -> 0 | cs -> List.max cs + 1
        let time, counter = if c0 > maxCounter then t + 1L, 0 else t, c0
        if time > maxTimeMs then
            Error Exhausted
        else
            let farFuture = time > now + driftLimitMs
            let newClock : Clock option =
                if farFuture then clock else Some { ClockMs = time; ClockCounter = counter }
            Ok { Stamp = { TimeMs = time; Counter = counter; Machine = machine }; Clock = newClock; FarFuture = farFuture }

    /// Moves the clock up to a stamp read from the repository, unless that stamp is far in the future.
    let observe (nowMs: int64) (clock: Clock option) (seen: Stamp) : ObserveResult =
        let now = clampNow nowMs
        if seen.TimeMs > now + driftLimitMs then
            { Clock = clock; FarFuture = true }
        else
            let s : Clock = { ClockMs = seen.TimeMs; ClockCounter = seen.Counter }
            let moved : Clock =
                match clock with
                | Some c when (c.ClockMs, c.ClockCounter) >= (s.ClockMs, s.ClockCounter) -> c
                | _ -> s
            { Clock = Some moved; FarFuture = false }

    /// A persisted clock more than `driftLimitMs` ahead of the wall clock is reset to it.
    let guardStartup (nowMs: int64) (clock: Clock option) : GuardResult =
        let now = clampNow nowMs
        match clock with
        | Some c when c.ClockMs > now + driftLimitMs ->
            { Clock = Some { ClockMs = now; ClockCounter = 0 }; Reset = true }
        | _ -> { Clock = clock; Reset = false }

    /// `<time ms>.<counter>`, the form stored in `_meta`.
    let serialize (c: Clock) : string =
        let inv = Globalization.CultureInfo.InvariantCulture
        c.ClockMs.ToString(inv) + "." + c.ClockCounter.ToString(inv)

    /// Strict inverse of `serialize`: ASCII digits only, no leading zeros, both parts in range.
    let tryDeserialize (s: string) : Clock option =
        if isNull s then None
        else
            let dot = s.IndexOf '.'
            if dot <= 0 || dot = s.Length - 1 || s.IndexOf('.', dot + 1) >= 0 then None
            else
                let timePart, counterPart = s.Substring(0, dot), s.Substring(dot + 1)
                let canonical (p: string) = String.forall isDigit p && (p.Length = 1 || p.[0] <> '0')
                if not (canonical timePart && canonical counterPart) || timePart.Length > 15 || counterPart.Length > 6 then None
                else
                    let t = Int64.Parse timePart
                    let c = Int32.Parse counterPart
                    if t > maxTimeMs || c > maxCounter then None else Some { ClockMs = t; ClockCounter = c }
