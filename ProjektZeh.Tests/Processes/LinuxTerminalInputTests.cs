using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V3 stdin: Terminalsitzung, read/readv/poll auf fd 0 und das Vordergrundprogramm der Shell.</summary>
[TestClass]
public sealed class LinuxTerminalInputTests
{
    private const ulong Base = 0x10000;

    private sealed class Lines : ITextOutput
    {
        public readonly List<string> All = new();
        public void WriteLine(string text) => All.Add(text);
        public void Clear() => All.Clear();
    }

    /// <summary>Schlaf im Test: zählt und führt je Aufruf eine Aktion aus (etwa eine Enter-Zeile "während" des Wartens).</summary>
    private sealed class Clock
    {
        public int Sleeps;
        public int SleptMs;
        public Action<int>? OnSleep;
        public void Sleep(int ms)
        {
            Sleeps++;
            SleptMs += ms;
            if (Sleeps > 10_000) throw new InvalidOperationException("wartet endlos");
            OnSleep?.Invoke(Sleeps);
        }
    }

    private sealed class Memory : IUserMemory
    {
        public readonly byte[] Data = new byte[0x4000];

        public bool TryRead(ulong addr, Span<byte> dst)
        {
            if (!UserRange.Contains(Base, Base + (ulong)Data.Length, addr, (ulong)dst.Length)) return false;
            Data.AsSpan((int)(addr - Base), dst.Length).CopyTo(dst);
            return true;
        }

        public bool TryWrite(ulong addr, ReadOnlySpan<byte> src)
        {
            if (!UserRange.Contains(Base, Base + (ulong)Data.Length, addr, (ulong)src.Length)) return false;
            src.CopyTo(Data.AsSpan((int)(addr - Base)));
            return true;
        }

        public bool CanWrite(ulong addr, ulong length) => UserRange.Contains(Base, Base + (ulong)Data.Length, addr, length);

        public bool TryReadCString(ulong addr, int max, out string s)
        {
            s = "";
            return false;
        }
    }

    private sealed class Proc(ILinuxDescriptor terminal) : ILinuxProcess, ILinuxHost, ILinuxAbort
    {
        public bool IsAborted { get; set; }
        public readonly Memory Mem = new();
        public IUserMemory Memory => Mem;
        public ILinuxDescriptor? GetDescriptor(int fd) => fd is >= 0 and <= 2 ? terminal : null;
        public ulong Brk => 0x400000;
        public bool TrySetBrk(ulong n) => false;
        public ulong MapAnonymous(ulong len, int prot) => 0;
        public bool Unmap(ulong a, ulong l) => false;
        public bool Protect(ulong a, ulong l, int p) => false;
        public bool MapFixed(ulong a, ulong l, int p) => false;
        public bool Discard(ulong a, ulong l) => false;
        public ulong Uid => 1000;
        public ulong Gid => 1000;
        public ulong FsBase { get; set; }
        public ulong ClearChildTid { get; set; }
        public int Pid => 1;
        public LinuxSignalState Signals { get; } = new();
        public long RealtimeNanos => 0;
        public long MonotonicNanos => 0;
        public long SleptNanos;
        public void SleepNanos(long ns) => SleptNanos += ns;
        public bool TryFillRandom(Span<byte> d) => false;
        public void Exit(int code) { }
    }

    private static long Call(Proc p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static (LinuxTerminalSession Session, TerminalDescriptor Terminal, Clock Clock, Proc Proc) Program()
    {
        var clock = new Clock();
        var session = new LinuxTerminalSession(clock.Sleep);
        var terminal = new TerminalDescriptor(session);
        return (session, terminal, clock, new Proc(terminal));
    }

    private static string Text(Proc p, int offset, long length) => Encoding.UTF8.GetString(p.Mem.Data, offset, (int)length);

    [TestMethod]
    public void ReadWartetAufEnterZeileUndLiefertSieMitZeilenende()
    {
        var (session, _, clock, p) = Program();
        clock.OnSleep = n => { if (n == 3) session.Submit("1+1"); };
        Assert.AreEqual(4, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual("1+1\n", Text(p, 0, 4));
        Assert.AreEqual(3, clock.Sleeps);
        Assert.IsTrue(clock.SleptMs <= 3 * LinuxTerminalSession.Slice, "Warten nur in Scheiben");
    }

    [TestMethod]
    public void FionreadZaehltEingegebeneBytes()
    {
        var (session, _, _, p) = Program();
        const ulong FIONREAD = 0x541B;
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, FIONREAD, Base));
        Assert.AreEqual(0, BitConverter.ToInt32(p.Mem.Data, 0), "leerer Eingabepuffer");
        session.Submit("abc");
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, FIONREAD, Base));
        Assert.AreEqual(4, BitConverter.ToInt32(p.Mem.Data, 0), "Zeile samt Zeilenende");
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Read, 0, Base + 0x100, 1));
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, FIONREAD, Base));
        Assert.AreEqual(3, BitConverter.ToInt32(p.Mem.Data, 0), "Rest nach read");
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Ioctl, 0, FIONREAD, 8));
    }

    [TestMethod]
    public void ReadLiefertHoechstensCountUndRestBeimNaechstenMal()
    {
        var (session, _, _, p) = Program();
        session.Submit("abcdef");
        Assert.AreEqual(4, Call(p, LinuxSyscalls.Read, 0, Base, 4));
        Assert.AreEqual(3, Call(p, LinuxSyscalls.Read, 0, Base + 0x100, 4));
        Assert.AreEqual("abcd", Text(p, 0, 4));
        Assert.AreEqual("ef\n", Text(p, 0x100, 3));
    }

    [TestMethod]
    public void StrgDLiefertEinmalEndeDannWiederZeilen()
    {
        var (session, _, clock, p) = Program();
        session.EndInput();
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Read, 0, Base, 10));
        clock.OnSleep = n => session.Submit("x");
        Assert.AreEqual(2, Call(p, LinuxSyscalls.Read, 0, Base, 10));
    }

    [TestMethod]
    public void AbbruchBeendetWartendesReadMitEintr()
    {
        var (session, _, clock, p) = Program();
        int calls = 0;
        session.OnInterrupt = () => calls++;
        clock.OnSleep = n => { if (n == 2) session.Interrupt(); };
        Assert.AreEqual(-LinuxSyscalls.EINTR, Call(p, LinuxSyscalls.Read, 0, Base, 10));
        session.Interrupt();
        Assert.AreEqual(1, calls, "zweiter Abbruch ruft den Kernel nicht erneut");
    }

    [TestMethod]
    public void ReadPrueftPufferVorDemWartenUndFd()
    {
        var (session, _, clock, p) = Program();
        session.Submit("verloren?");
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Read, 0, 0x10, 4));
        Assert.AreEqual(-LinuxSyscalls.EBADF, Call(p, LinuxSyscalls.Read, 7, Base, 4));
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Read, 0, Base, 0));
        Assert.AreEqual(0, clock.Sleeps);
        Assert.AreEqual(10, Call(p, LinuxSyscalls.Read, 0, Base, 64), "Zeile blieb nach EFAULT erhalten");
    }

    [TestMethod]
    public void ReadOhneSitzungIstLeerWieDevNull()
    {
        var terminal = new TerminalDescriptor(new Lines());
        var p = new Proc(terminal);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Read, 0, Base, 10));
        Assert.IsTrue(terminal.InputReady);
    }

    [TestMethod]
    public void ReadvVerteiltEineZeileAufDieEintraege()
    {
        var (session, _, _, p) = Program();
        session.Submit("hallo");
        // iov[0] = 2 Bytes bei Base+0x100, iov[1] = 10 Bytes bei Base+0x200 (so liest musl: Benutzerpuffer, dann FILE-Puffer).
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0, 8), Base + 0x100);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(8, 8), 2UL);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(16, 8), Base + 0x200);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(24, 8), 10UL);
        Assert.AreEqual(6, Call(p, LinuxSyscalls.Readv, 0, Base, 2));
        Assert.AreEqual("ha", Text(p, 0x100, 2));
        Assert.AreEqual("llo\n", Text(p, 0x200, 4));
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(16, 8), 0x10UL); // ungültiger zweiter Eintrag
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Readv, 0, Base, 2));
    }

    [TestMethod]
    public void PollMeldetEingabeUndWartetOhneFristAufEnter()
    {
        var (session, _, clock, p) = Program();
        // pollfd { fd 0, POLLIN }
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0, 4), 0);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(4, 2), (short)1);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Poll, Base, 1, 0), "Frist 0: nicht bereit, kein Warten");
        Assert.AreEqual(0, clock.Sleeps);
        clock.OnSleep = n => { if (n == 5) session.Submit("x"); };
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Poll, Base, 1, unchecked((ulong)-1)));
        Assert.AreEqual(1, BitConverter.ToInt16(p.Mem.Data, 6));
        Assert.AreEqual(5, clock.Sleeps);
        Assert.AreEqual(0, p.SleptNanos, "kein zweites Schlafen über den Host");
    }

    [TestMethod]
    public void PollMitFristEndetOhneEingabeNachDerFrist()
    {
        var (_, _, clock, p) = Program();
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0, 4), 0);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(4, 2), (short)1);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Poll, Base, 1, 120));
        Assert.AreEqual(120, clock.SleptMs);
    }

    [TestMethod]
    public void AngefangeneZeileWirdBeimLesenZurEingabeaufforderung()
    {
        var (session, terminal, clock, p) = Program();
        var shown = new Lines();
        terminal.Write("Willkommen\n> "u8);
        clock.OnSleep = n =>
        {
            if (n != 1) return;
            Assert.AreEqual("> ", session.Prompt);
            session.Submit("2*3");
        };
        Assert.AreEqual(4, Call(p, LinuxSyscalls.Read, 0, Base, 64));
        Assert.AreEqual("", session.Prompt, "Zeile ist mit der Eingabe ausgegeben");
        terminal.Write("6\n"u8);
        session.Drain(shown);
        Assert.AreEqual("Willkommen|6", string.Join("|", shown.All));
    }

    [TestMethod]
    public void AufforderungErscheintAuchBeimWartenInPoll()
    {
        var (session, terminal, clock, p) = Program();
        var shown = new Lines();
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(0, 4), 0);
        BitConverter.TryWriteBytes(p.Mem.Data.AsSpan(4, 2), (short)1);
        terminal.Write("> "u8);
        clock.OnSleep = n =>
        {
            if (n != 1) return;
            Assert.AreEqual("> ", session.Prompt, "REPL wartet in poll, nicht in read");
            session.Submit("quit");
        };
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Poll, Base, 1, unchecked((ulong)-1)));
        Assert.AreEqual(5, Call(p, LinuxSyscalls.Read, 0, Base + 0x100, 64));
        terminal.Write("tschuess\n"u8);
        session.Drain(shown);
        Assert.AreEqual("tschuess", string.Join("|", shown.All));
    }

    [TestMethod]
    public void UnbeantworteteAufforderungGehtBeiWeitererAusgabeInDieZeile()
    {
        var (session, terminal, clock, p) = Program();
        var shown = new Lines();
        terminal.Write("> "u8);
        clock.OnSleep = n => { if (n == 1) session.EndInput(); };
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Read, 0, Base, 64));
        Assert.AreEqual("> ", session.Prompt);
        terminal.Write("tschuess\n"u8);
        Assert.AreEqual("", session.Prompt);
        session.Drain(shown);
        Assert.AreEqual("> tschuess", string.Join("|", shown.All));
    }

    [TestMethod]
    public void VolleWarteschlangeLaesstDasProgrammWartenNichtDieShell()
    {
        var clock = new Clock();
        var session = new LinuxTerminalSession(clock.Sleep);
        var shown = new Lines();
        for (int i = 0; i < LinuxTerminalSession.MaxQueuedLines; i++) session.WriteLine("z" + i);
        clock.OnSleep = n => { if (n == 2) session.Drain(shown); };
        session.WriteLine("danach");
        Assert.AreEqual(2, clock.Sleeps);
        Assert.AreEqual(LinuxTerminalSession.MaxQueuedLines, shown.All.Count);
        session.Drain(shown);
        Assert.AreEqual("danach", shown.All[^1]);
    }

    [TestMethod]
    public void StatuszeileKommtNachDerLetztenAusgabe()
    {
        var session = new LinuxTerminalSession(_ => { });
        var shown = new Lines();
        session.WriteLine("a");
        session.Finish("[exit 0]");
        session.WriteLine("b"); // kann nach Finish nicht vorkommen, aber auch dann bleibt die Reihenfolge
        Assert.IsFalse(session.IsDone);
        Assert.IsTrue(session.Drain(shown));
        Assert.AreEqual("a|b|[exit 0]", string.Join("|", shown.All));
        Assert.IsTrue(session.IsDone);
        Assert.IsFalse(session.Drain(shown));
    }

    // ---- Shell: Vordergrundprogramm ----

    [TestMethod]
    public void ShellGibtZeilenAnDasProgrammUndNichtAnDieBefehle()
    {
        var shell = new ShellService();
        var session = new LinuxTerminalSession(_ => { });
        var shown = new Lines();
        Assert.IsTrue(shell.AttachForeground(session).IsOK());
        Assert.IsFalse(shell.AttachForeground(new LinuxTerminalSession(_ => { })).IsOK());
        Assert.IsTrue(shell.HasForeground);
        Assert.IsFalse(shell.CanRemember("echo geheim"), "REPL-Eingaben nicht im Shellverlauf");
        Assert.IsTrue(shell.ExecuteLine("echo nicht ausfuehren", shown).IsOK());
        CollectionAssert.AreEqual(Array.Empty<string>(), shown.All);
        Span<byte> got = new byte[64];
        long n = session.ReadInput(got);
        Assert.AreEqual("echo nicht ausfuehren\n", Encoding.UTF8.GetString(got[..(int)n]));
    }

    [TestMethod]
    public void PollHoltAusgabeUndGibtDieShellNachDemEndeFrei()
    {
        var shell = new ShellService();
        var session = new LinuxTerminalSession(_ => { });
        var shown = new Lines();
        shell.AttachForeground(session);
        session.WriteLine("hallo");
        Assert.IsTrue(shell.Poll(shown));
        Assert.IsTrue(shell.HasForeground);
        session.Finish("[exit 0]");
        Assert.IsTrue(shell.Poll(shown));
        Assert.IsFalse(shell.HasForeground);
        Assert.AreEqual("hallo|[exit 0]", string.Join("|", shown.All));
        Assert.IsTrue(shell.ExecuteLine("echo wieder da", shown).IsOK());
        Assert.AreEqual("wieder da", shown.All[^1]);
    }

    [TestMethod]
    public void StrgCBrichtAbUndZweitesStrgCGibtDasTerminalFrei()
    {
        var shell = new ShellService();
        var session = new LinuxTerminalSession(_ => { });
        var shown = new Lines();
        int interrupts = 0;
        session.OnInterrupt = () => interrupts++;
        shell.AttachForeground(session);
        Assert.IsTrue(shell.TryInterrupt(shown));
        Assert.AreEqual(1, interrupts);
        Assert.IsTrue(shell.HasForeground, "erst das Programmende gibt frei");
        Assert.IsTrue(shell.TryInterrupt(shown));
        Assert.IsFalse(shell.HasForeground, "haengendes Programm: zweiter Abbruch gibt frei");
        Assert.AreEqual(1, interrupts);
        session.WriteLine("verfaellt");
        Assert.IsFalse(shell.Poll(shown));
        Assert.AreEqual(-LinuxSyscalls.EINTR, session.ReadInput(new byte[4]));
        Assert.IsFalse(shell.TryInterrupt(shown), "ohne Programm und Auftrag nichts abzubrechen");
    }

    [TestMethod]
    public void StrgDUndSchliessenErreichenDasProgramm()
    {
        var shell = new ShellService();
        var session = new LinuxTerminalSession(_ => { });
        int interrupts = 0;
        session.OnInterrupt = () => interrupts++;
        Assert.IsFalse(shell.EndForegroundInput());
        shell.AttachForeground(session);
        Assert.IsTrue(shell.EndForegroundInput());
        Assert.AreEqual(0, session.ReadInput(new byte[4]));
        shell.Close();
        Assert.AreEqual(1, interrupts);
        Assert.IsFalse(shell.HasForeground);
    }

    // ---- termios (V3 termios): isatty, Zeilenpuffer, REPL-Erkennung ----

    private static uint U32(Proc p, int offset) => BitConverter.ToUInt32(p.Mem.Data, offset);

    [TestMethod]
    public void TcgetsLiefertKanonischMitEchoAufAllenDreiFds()
    {
        var (_, _, _, p) = Program();
        for (ulong fd = 0; fd <= 2; fd++)
        {
            Array.Clear(p.Mem.Data);
            Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, fd, LinuxSyscalls.TCGETS, Base));
            uint lflag = U32(p, 12);
            Assert.AreEqual(0x2u, lflag & 0x2, "ICANON");
            Assert.AreEqual(0x8u, lflag & 0x8, "ECHO");
            Assert.AreEqual(0x1u, lflag & 0x1, "ISIG");
            Assert.AreEqual(0x5u, U32(p, 4), "OPOST ONLCR");
            Assert.AreEqual(0x03, p.Mem.Data[17 + 0], "VINTR ^C");
            Assert.AreEqual(0x04, p.Mem.Data[17 + 4], "VEOF ^D");
            Assert.AreEqual(0x01, p.Mem.Data[17 + 6], "VMIN 1");
            Assert.AreEqual(0, p.Mem.Data[LinuxSyscalls.TermiosSize], "nur die Kernelstruktur, nichts dahinter");
        }
    }

    [TestMethod]
    public void TcsetsSpeichertRohmodusFuerAlleFdsUndLiestZeilenWeiter()
    {
        var (session, _, _, p) = Program();
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCGETS, Base));
        // cfmakeraw: ICANON, ECHO, ISIG aus; VMIN 1, VTIME 0
        uint raw = U32(p, 12) & ~0xbu;
        BitConverter.GetBytes(raw).CopyTo(p.Mem.Data, 12);
        foreach (ulong request in new[] { LinuxSyscalls.TCSETS, LinuxSyscalls.TCSETSW, LinuxSyscalls.TCSETSF })
        {
            Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, request, Base));
        }

        Array.Clear(p.Mem.Data);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 1, LinuxSyscalls.TCGETS, Base + 0x100));
        Assert.AreEqual(raw, U32(p, 0x100 + 12), "fd 1 sieht, was fd 0 gesetzt hat");

        // V3 raw: Rohmodus wirkt, ohne ISIG bricht Strg+C nicht ab.
        Assert.IsTrue(session.RawInput);
        Assert.IsFalse(session.SignalKeys);
        session.SubmitRaw("abc"u8.ToArray());
        Assert.AreEqual(3, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual("abc", Text(p, 0, 3));
    }

    [TestMethod]
    public void WinszKommtAusDemFensterUndHatEinenErsatz()
    {
        var (session, _, _, p) = Program();
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 1, LinuxSyscalls.TIOCGWINSZ, Base));
        Assert.AreEqual(24, (int)BitConverter.ToUInt16(p.Mem.Data, 0));
        Assert.AreEqual(80, (int)BitConverter.ToUInt16(p.Mem.Data, 2));

        var shell = new ShellService();
        session.SizeSource = () => shell.WindowSize;
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 1, LinuxSyscalls.TIOCGWINSZ, Base));
        Assert.AreEqual(24, (int)BitConverter.ToUInt16(p.Mem.Data, 0), "Shell ohne Fenstermeldung");
        shell.SetWindowSize(37, 112);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 2, LinuxSyscalls.TIOCGWINSZ, Base));
        Assert.AreEqual(37, (int)BitConverter.ToUInt16(p.Mem.Data, 0));
        Assert.AreEqual(112, (int)BitConverter.ToUInt16(p.Mem.Data, 2));
        Assert.AreEqual(0u, BitConverter.ToUInt32(p.Mem.Data, 4), "Pixelgröße 0");
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 1, LinuxSyscalls.TIOCSWINSZ, Base), "angenommen, ohne Wirkung");
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Ioctl, 1, LinuxSyscalls.TIOCGWINSZ, 8));
    }

    [TestMethod]
    public void VordergrundgruppeIstDerProzess()
    {
        var (_, _, _, p) = Program();
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TIOCGPGRP, Base));
        Assert.AreEqual(p.Pid, BitConverter.ToInt32(p.Mem.Data, 0));
        BitConverter.GetBytes(p.Pid).CopyTo(p.Mem.Data, 0x10);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TIOCSPGRP, Base + 0x10));
        BitConverter.GetBytes(77).CopyTo(p.Mem.Data, 0x10);
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TIOCSPGRP, Base + 0x10));
        BitConverter.GetBytes(-1).CopyTo(p.Mem.Data, 0x10);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TIOCSPGRP, Base + 0x10));
    }

    [TestMethod]
    public void OhneSitzungKeinTerminalUndFehlerWieLinux()
    {
        var p = new Proc(new TerminalDescriptor(new Lines()));
        Assert.AreEqual(-LinuxSyscalls.ENOTTY, Call(p, LinuxSyscalls.Ioctl, 1, LinuxSyscalls.TIOCGWINSZ, Base), "serieller Prüflauf bleibt voll gepuffert");
        Assert.AreEqual(-LinuxSyscalls.ENOTTY, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCGETS, Base));

        var (_, _, _, q) = Program();
        Assert.AreEqual(-LinuxSyscalls.EBADF, Call(q, LinuxSyscalls.Ioctl, 3, LinuxSyscalls.TCGETS, Base));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(q, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCGETS, 8));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(q, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCSETS, 8));
    }

    [TestMethod]
    public void ShellMeldetFenstergroesseGeklemmt()
    {
        var shell = new ShellService();
        Assert.AreEqual(((ushort)24, (ushort)80), shell.WindowSize);
        shell.SetWindowSize(0, 100000);
        Assert.AreEqual(((ushort)1, (ushort)0xffff), shell.WindowSize);
    }

    // ---- Rohmodus (V3 raw): Tasten einzeln, Zeileneditor im Programm, Prozessgruppen ----

    /// <summary>Schaltet wie LibLine ICANON und ECHO aus (ISIG und ICRNL bleiben); mit <paramref name="cfmakeraw"/> auch ISIG und ICRNL.</summary>
    private static void SetRaw(Proc p, bool cfmakeraw = false)
    {
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCGETS, Base + 0x800));
        uint iflag = U32(p, 0x800), lflag = U32(p, 0x800 + 12);
        lflag &= ~(LinuxSyscalls.ICANON | 0x8u);
        if (cfmakeraw)
        {
            lflag &= ~LinuxSyscalls.ISIG;
            iflag &= ~LinuxSyscalls.ICRNL;
        }

        BitConverter.GetBytes(iflag).CopyTo(p.Mem.Data, 0x800);
        BitConverter.GetBytes(lflag).CopyTo(p.Mem.Data, 0x800 + 12);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCSETS, Base + 0x800));
    }

    [TestMethod]
    public void RohmodusLiefertJedeTasteSofortUndEnterAlsZeilenende()
    {
        var (session, _, clock, p) = Program();
        Assert.IsFalse(session.RawInput);
        Assert.AreEqual(-1, session.PromptCursor);
        SetRaw(p);
        Assert.IsTrue(session.RawInput);
        Assert.IsTrue(session.SignalKeys, "LibLine laesst ISIG an");
        clock.OnSleep = n => { if (n == 2) session.SubmitRaw(new byte[] { (byte)'x' }); };
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual("x", Text(p, 0, 1));
        session.SubmitRaw(new byte[] { 0x1b, (byte)'[', (byte)'D', (byte)'\r' });
        Assert.AreEqual(4, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual("\x1b[D\n", Text(p, 0, 4), "ICRNL: CR wird LF");

        // Zurück in den kanonischen Modus (LibLine beim Beenden): wieder ganze Zeilen.
        LinuxSyscalls.DefaultTermios().CopyTo(p.Mem.Data, 0x1000);
        Assert.AreEqual(0, Call(p, LinuxSyscalls.Ioctl, 0, LinuxSyscalls.TCSETS, Base + 0x1000));
        Assert.IsFalse(session.RawInput);
        Assert.AreEqual(-1, session.PromptCursor);
    }

    [TestMethod]
    public void CfmakerawLaesstCrUndStrgCDurch()
    {
        var (session, _, _, p) = Program();
        SetRaw(p, cfmakeraw: true);
        Assert.IsFalse(session.SignalKeys);
        session.SubmitRaw(new byte[] { (byte)'\r', 0x03 });
        Assert.AreEqual(2, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual((byte)'\r', p.Mem.Data[0]);
        Assert.AreEqual(0x03, p.Mem.Data[1]);

        var shell = new ShellService();
        Assert.IsTrue(shell.AttachForeground(session).IsOK());
        Assert.IsTrue(shell.ForegroundRawInput);
        Assert.IsTrue(shell.ForegroundTakesInterruptKey, "ohne ISIG geht Strg+C als Byte an das Programm");
        Assert.IsTrue(shell.SendForegroundInput(new byte[] { (byte)'q' }));
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual((byte)'q', p.Mem.Data[0]);
    }

    [TestMethod]
    public void FrageNachCursorpositionWirdBeantwortetUndNichtAngezeigt()
    {
        var (session, terminal, _, p) = Program();
        var shell = new ShellService();
        shell.SetWindowSize(30, 100);
        session.SizeSource = () => shell.WindowSize;
        SetRaw(p);
        // LibLine: Prompt zeichnen, dann über fd 2 nach der Position fragen; die Frage kann über zwei write verteilt sein.
        terminal.Write("> ab\x1b["u8);
        terminal.Write("6n"u8);
        Assert.AreEqual(7, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual("\x1b[30;5R", Text(p, 0, 7), "letzte Zeile, Spalte hinter \"> ab\"");
        terminal.Write("\n"u8);
        var shown = new Lines();
        session.Drain(shown);
        Assert.AreEqual("> ab", string.Join("|", shown.All));
    }

    [TestMethod]
    public void AntwortenAufCursorfragenSindBegrenzt()
    {
        var (session, terminal, _, p) = Program();
        SetRaw(p);
        // Review V3 raw P2: Ein Programm, das fragt und nie liest, darf die Eingabe nicht ohne Grenze wachsen lassen.
        for (int i = 0; i < 100_000; i++)
        {
            terminal.Write("\x1b[6n"u8);
        }

        int total = 0;
        while (session.InputReady)
        {
            long n = Call(p, LinuxSyscalls.Read, 0, Base, 0x4000);
            Assert.IsTrue(n > 0);
            total += (int)n;
        }

        Assert.IsTrue(total <= LinuxTerminalSession.MaxInputBytes + LinuxTerminalSession.ResponseReserve, $"{total} Byte gepuffert");
        Assert.IsTrue(total >= LinuxTerminalSession.MaxInputBytes, "bis zur Grenze wird geantwortet");
    }

    [TestMethod]
    public void CursorfrageBeiVollerEingabeWirdNochBeantwortet()
    {
        var (session, terminal, _, p) = Program();
        SetRaw(p);
        session.SubmitRaw(new byte[LinuxTerminalSession.MaxInputBytes]);
        terminal.Write("\x1b[6n"u8);
        long last = 0;
        while (session.InputReady)
        {
            last = Call(p, LinuxSyscalls.Read, 0, Base, 0x4000);
        }

        Assert.AreEqual((byte)'R', p.Mem.Data[last - 1], "Antwort steht hinter der vollen Eingabe");
    }

    [TestMethod]
    public void ZuKurzesTermiosWirdAbgelehnt()
    {
        var (_, terminal, _, _) = Program();
        Assert.ThrowsException<ArgumentException>(() => terminal.Termios = new byte[12]);
        Assert.AreEqual(LinuxSyscalls.TermiosSize, terminal.Termios.Length, "Zustand unveraendert");
    }

    [TestMethod]
    public void RohmodusZeigtGezeichneteZeileMitCursorUndGibtNurDenStandAus()
    {
        var (session, terminal, clock, p) = Program();
        SetRaw(p);
        // Neuzeichnen wie LibLine: an den Anfang, Zeile leeren, Prompt und Text, Cursor zwei Zeichen zurück.
        terminal.Write("\r\x1b[K> 1+2\x1b[2D"u8);
        clock.OnSleep = n =>
        {
            if (n != 1) return;
            Assert.AreEqual("> 1+2", session.Prompt);
            Assert.AreEqual(3, session.PromptCursor);
            session.SubmitRaw(new byte[] { (byte)'0' });
        };
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        terminal.Write("\r\x1b[K> 10+2\x1b[2D"u8);
        terminal.Write("\x1b[2C\n"u8);
        var shown = new Lines();
        session.Drain(shown);
        Assert.AreEqual("> 10+2", string.Join("|", shown.All), "Neuzeichnungen erscheinen nicht als eigene Zeilen");
    }

    [TestMethod]
    public void RohmodusVerdichtetLangeNeuzeichnungenStattZeilenAuszugeben()
    {
        var (session, terminal, _, p) = Program();
        SetRaw(p);
        for (int i = 0; i < 2000; i++)
        {
            terminal.Write(Encoding.UTF8.GetBytes("\r\x1b[K> äbc" + (i % 10) + "\x1b[1D"));
        }

        terminal.Write("\n"u8);
        var shown = new Lines();
        session.Drain(shown);
        Assert.AreEqual("> äbc9", string.Join("|", shown.All));
    }

    [TestMethod]
    public void KanonischBleibtDieZeilendarstellungUnveraendert()
    {
        var (session, terminal, _, _) = Program();
        terminal.Write("ab\rX\x1b[1Dz\n"u8);
        var shown = new Lines();
        session.Drain(shown);
        Assert.AreEqual("Xz", string.Join("|", shown.All), "Printable wie bisher, nicht der Rohmodus-Interpreter");
    }

    [TestMethod]
    public void ProzessgruppeUndSitzungSindDerProzess()
    {
        var (_, _, _, p) = Program();
        Assert.AreEqual(p.Pid, Call(p, LinuxSyscalls.Getpgrp));
        Assert.AreEqual(p.Pid, Call(p, LinuxSyscalls.Getpgid, 0));
        Assert.AreEqual(p.Pid, Call(p, LinuxSyscalls.Getpgid, (ulong)p.Pid));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Call(p, LinuxSyscalls.Getpgid, 77));
        Assert.AreEqual(p.Pid, Call(p, LinuxSyscalls.Getsid, 0));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Call(p, LinuxSyscalls.Getsid, 77));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Call(p, LinuxSyscalls.Getpgid, unchecked((ulong)-1L)), "Linux: negative PID nicht gefunden");
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Call(p, LinuxSyscalls.Getsid, unchecked((ulong)-5L)));
        Assert.AreEqual(-LinuxSyscalls.EPERM, Call(p, LinuxSyscalls.Setpgid, 0, 0), "Sitzungsfuehrer, wie Linux im neuen pty");
        Assert.AreEqual(-LinuxSyscalls.EPERM, Call(p, LinuxSyscalls.Setpgid, (ulong)p.Pid, (ulong)p.Pid));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, Call(p, LinuxSyscalls.Setpgid, 77, 0));
        Assert.AreEqual(-LinuxSyscalls.EPERM, Call(p, LinuxSyscalls.Setpgid, 0, 77));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Call(p, LinuxSyscalls.Setpgid, 0, unchecked((ulong)-1L)));
        Assert.AreEqual(-LinuxSyscalls.EPERM, Call(p, LinuxSyscalls.Setsid), "schon Gruppenfuehrer");
    }

    [TestMethod]
    public void NachDemAbbruchBleibtDieAusgabeBegrenztUndWartetNicht()
    {
        // Issue #735 P2-2: Nach Strg+C wartet WriteLine nicht mehr, hält aber die Grenze (vorher wuchs die Warteschlange ohne Ende).
        var (session, _, clock, _) = Program();
        session.Interrupt();
        for (int i = 0; i < 3 * LinuxTerminalSession.MaxQueuedLines; i++) session.WriteLine("z" + i);
        Assert.AreEqual(0, clock.Sleeps);
        var shown = new Lines();
        session.Drain(shown);
        Assert.IsTrue(shown.All.Count <= LinuxTerminalSession.MaxQueuedLines + 1);
    }

    [TestMethod]
    public void LangeKopienPruefenDenAbbruchJeStueck()
    {
        // Issue #735 P2-2: write/writev und getrandom enden nach dem Abbruch mit dem bisher Kopierten oder -EINTR.
        var (session, _, _, p) = Program();
        session.OnInterrupt = () => p.IsAborted = true;
        p.Mem.Data.AsSpan(0, 0x3000).Fill((byte)'a');
        Assert.AreEqual(100, Call(p, LinuxSyscalls.Write, 1, Base, 100));

        session.Interrupt();
        Assert.AreEqual(-LinuxSyscalls.EINTR, Call(p, LinuxSyscalls.Write, 1, Base, 0x3000));
        Assert.AreEqual(-LinuxSyscalls.EINTR, Call(p, LinuxSyscalls.Getrandom, Base, 0x1000));
    }

    [TestMethod]
    public void StrgUmschaltEscBrichtAuchImRohmodusOhneIsigAb()
    {
        // Issue #735 P2-3: Im Rohmodus ohne ISIG gehen Strg+C und Esc als Bytes an das Programm; Strg+Umschalt+Esc bricht trotzdem ab.
        var (session, _, _, p) = Program();
        SetRaw(p, cfmakeraw: true);
        var shell = new ShellService();
        Assert.IsTrue(shell.AttachForeground(session).IsOK());
        var terminal = new TerminalApp(shell, new byte[256 * 16], 8, 16, 80);

        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Escape));
        Assert.IsFalse(session.IsInterrupted, "Esc allein ist im Rohmodus eine Taste");
        Assert.AreEqual(1, Call(p, LinuxSyscalls.Read, 0, Base, 100));
        Assert.AreEqual(0x1b, p.Mem.Data[0]);

        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Escape));
        Assert.IsTrue(session.IsInterrupted);

        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Escape));
        Assert.IsFalse(shell.HasForeground, "zweiter Druck gibt das Fenster frei");
    }
}
