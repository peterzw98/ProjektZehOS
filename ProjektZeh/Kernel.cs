using System;
using System.Diagnostics;
using ProjektZeh.Runtime;
using ProjektZeh.Installer;
using ProjektZeh.Update;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.SystemServices;
using ProjektZeh.Shared;
using ProjektZeh.Security;
using ProjektZeh.Scheduling;
using ProjektZeh.Messaging;
using ProjektZeh.Network;
using ProjektZeh.Packages;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Input;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Timers;
using Sys = Cosmos.Kernel.System;

namespace ProjektZeh;

public sealed class Kernel : Sys.Kernel
{
    private readonly CosmosStorageService storage = new();
#if ZEHOS_ROOT_MIGRATION
    // Etappe 4.5: jeder Schreiber auf /data nimmt einen Lease, damit system migrate die Quelle exklusiv sperren kann.
    private static readonly Func<IDisposable?>? SourceWrites = AppStorageGate.TryBeginWrite;
    private static readonly Func<IDisposable?>? PackageWrites = () => AppStorageGate.TryBeginWrite("Paketinstallation");
#else
    // Ohne Flag bleiben beide null: die Dienste verhalten sich wie vor 4.5.
    private static Func<IDisposable?>? SourceWrites => null;
    private static Func<IDisposable?>? PackageWrites => null;
#endif
    private readonly ConsoleTextOutput output = new();
    // Entsteht erst in InitializeServices, weil die Shell Konten, Pakete und das Pfadprofil braucht.
    private InteractiveSession interactive = null!;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SystemLog systemLog;
    private readonly CosmosSystemFiles systemFiles;
    private bool startDesktop;
    private static UpdateVersion CurrentVersion
    {
        get
        {
            var version = typeof(Kernel).Assembly.GetName().Version!;
            return new UpdateVersion(version.Major, version.Minor, version.Build);
        }
    }
    // Etappe 4.3: das Pfadprofil wird einmal beim Boot gewaehlt (InitializeServices); Konten, Pakete und Updates haengen davon ab
    // und entstehen deshalb erst dort, nicht als Feldinitialisierer vor jeder Plattenpruefung.
    private SystemPaths systemPaths = SystemPaths.Legacy;
#if ZEHOS_INSTALLER
    private readonly IInstallService installer;
#else
    private UpdateController updates = null!;
#endif
    private AccountService accounts = null!;
#if ZEHOS_ROOT_PROFILE
    // Etappe 4.3: Ergebnis der Profilwahl; RootVolume nur im schreibbaren Root-Fall (s_state beim Herunterfahren).
    private CosmosLayoutSelection? layout;
#endif
    private PackageStore packages = null!;
    private static bool SecurityRecovery =>
#if ZEHOS_SECURITY_RECOVERY
        true;
#else
        false;
#endif
    private readonly BackgroundTaskService tasks = new();
    private readonly CosmosNetworkService networkService = new();
    private readonly NetworkAutoConfiguration automaticNetwork;
    private readonly CosmosRoundRobinScheduler scheduler = new();
    private readonly MessageDemoService messages;
    private UserSession desktopIdentity = null!;

    public Kernel()
    {
        messages = new MessageDemoService(tasks);
        systemLog = new SystemLog(128, GetElapsedMilliseconds, new SerialLogSink());
        // Lange Warte- oder Haltezeiten der Speicher-Sperre nur seriell melden: das Protokoll selbst schreibt keine Datei.
        StorageLock.Report = text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("STORAGE " + text + "\n");
        systemFiles = new CosmosSystemFiles(VersionText, GetElapsedMilliseconds, systemLog);
        automaticNetwork = new(networkService, tasks,
            text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("NET " + text + "\n"));
#if ZEHOS_INSTALLER
        installer = new CosmosInstallService(CurrentVersion.ToString(), () =>
        {
            var prepared = PreparePowerAction();
            if (!prepared.IsOK()) return prepared;
            Sys.Power.Reboot();
            return ResultErr.CreateSuccess();
        });
#endif
    }

    /// <summary>
    /// Baut die profilabhaengigen Dienste genau einmal beim Boot, vor dem ersten Zugriff im Storage-Schritt (Etappe 4.3).
    /// Reihenfolge wie frueher im Konstruktor: Updates, Konten, Pakete, dann Sitzung und Shell.
    /// </summary>
    private void InitializeServices(SystemPaths paths)
    {
        systemPaths = paths;
#if !ZEHOS_INSTALLER
        updates = new(CurrentVersion, endpoint => new CosmosHttpUpdateSource(endpoint),
            new FileUpdateTarget(systemPaths.UpdateRoot, CosmosDirectoryEntryLimit.ForDirectory, SourceWrites), systemPaths.AdminRoot,
            CosmosDirectoryEntryLimit.ForDirectory, SourceWrites);
#endif
        // /data ist FAT: Vergleich ohne Gross-/Kleinschreibung; der Waechter sperrt Symlinks und fremde Mounts (Etappe 3).
        accounts = new(systemPaths.AdminRoot, new PasswordHasher(CosmosSaltSource.Fill), systemPaths.Comparison,
            CosmosPathGuard.For(systemPaths.AdminRoot), CosmosDirectoryEntryLimit.ForDirectory, systemPaths.SecurityDirectory, SourceWrites)
        {
            MutationBlockedReason = MutationBlockedReason,
        };
        // Die Ersteinrichtung setzt ein tatsaechlich eingebundenes /data voraus (nicht nur eine fehlende Datei).
        accounts.IsDataMounted = () => StorageLock.Run(() => VfsManager.TryGetMount(systemPaths.AdminRoot, out _));
        // Unter einem .zehos-Verzeichnis, das der normale Dateidienst nicht freigibt; nur PackageStore schreibt dort.
        packages = new(systemPaths.PackagesDirectory, PackageTrust.FromBuild(), CurrentVersion, PackageWrites);
        desktopIdentity = accounts.CreateSession();
#if ZEHOS_WASM
        // Etappe 13.4: App-Speicher mit demselben Waechter wie die Kontendatei.
        Runtime.Wasm.WasmRuntime.SavePathGuard = CosmosPathGuard.For(systemPaths.AdminRoot);
        Runtime.Wasm.WasmRuntime.SaveDiskSpace = CosmosSystemInfo.DiskSpace;
        Runtime.Wasm.WasmRuntime.SaveComparison = systemPaths.Comparison;
#endif
        interactive = new InteractiveSession(CreateShell(desktopIdentity), () => CreateTerminalShell(desktopIdentity),
            () => new FileService(desktopIdentity.HomeDirectory ?? systemPaths.StartDirectory, access: accounts.Access, session: desktopIdentity,
                diskSpace: CosmosSystemInfo.DiskSpace, directoryEntryLimit: CosmosDirectoryEntryLimit.ForDirectory,
                virtualSource: systemFiles.Produce, isRegularFile: CosmosSystemFiles.IsRegularFile),
            output, GetElapsedMilliseconds, systemLog, accounts, desktopIdentity, new CosmosSystemInfo(storage, VersionText).Sections,
#if ZEHOS_WASM
            ListPackageApplications, () => networkService.Status);
#else
            null, () => networkService.Status);
#endif
    }

    /// <summary>
    /// Sperrgrund vor jeder Kontenmutation (7a): mit Profilwahl zuerst deren Grund, der fuer die ganze Sitzung gilt
    /// (nicht ruecksetzbar), sonst die Identitaet des aktuell als /data eingehaengten Volumes.
    /// </summary>
    private string? MutationBlockedReason()
    {
#if ZEHOS_ROOT_PROFILE
        if (layout?.Decision.BlockedReason is { } blocked) return blocked;
#endif
        return CosmosInstalledIdentity.MutationBlockedReason();
    }

#if ZEHOS_ROOT_PROFILE
    /// <summary>Ein Root-Marker fresh wird nach dem ersten erfolgreichen Speichern der Konten auf ready gesetzt (Spec Abschnitt 2).</summary>
    private void ApplyLayout()
    {
        if (layout is null) return;
        if (!layout.Decision.MarkReadyOnSave || layout.RootVolume is not { } volume) return;
        bool marked = false;
        accounts.Saved = () =>
        {
            if (marked) return;
            var result = CosmosLayoutProfile.MarkReady(layout.DiskGuid, volume);
            if (result.IsOK()) marked = true;
            else systemLog.Error("Storage", result.Message);
        };
    }

#endif
    /// <summary>Neue Terminals starten als angemeldeter Desktopbenutzer, behalten danach aber ihre eigene Identitaet.</summary>
    private ShellService CreateTerminalShell(UserSession desktopIdentity)
    {
        var identity = accounts.CreateSession();
        identity.LoginAs(desktopIdentity);
        return CreateShell(identity);
    }

#if ZEHOS_WASM
    // Startmenueeintraege aus dem Paketbestand; beschaedigte oder nicht signierte Pakete erscheinen nicht.
    private Apps.ExternalApplication[] ListPackageApplications()
    {
        var installed = packages.ReadInstalled();
        if (!installed.IsOK()) return [];
        var apps = new Apps.ExternalApplication[installed.Value!.Length];
        for (int index = 0; index < apps.Length; index++)
        {
            string id = installed.Value[index].Manifest.Id;
            apps[index] = new(id, installed.Value[index].Manifest.Name,
                painter => Runtime.Wasm.WasmRuntime.IsBusy ? null : new Apps.Wasm.WasmGameApp(packages, id, painter,
                    text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("APP " + id + " " + text + "\n"), SaveRootFor));
        }
        return apps;
    }

    /// <summary>
    /// Etappe 13.4: /save einer App fuer das angemeldete Desktopkonto, nur mit eingebundenem /data. Der Ordner wird bei
    /// Bedarf ueber einen Dateidienst ohne Rechteverwaltung mit Waechter angelegt; ohne ihn laeuft die App ohne Speichern.
    /// </summary>
    private string? SaveRootFor(string packageId)
    {
        if (!accounts.IsDataMounted() || desktopIdentity.UserId is not { } user) return null;
        var path = systemPaths.AppState(packageId, user);
        if (!path.IsOK()) return null;
        // #210 (ebenenweise anlegen) mit dem Profilpfad aus 4.3; im Root-Profil liegt der App-Zustand unter /var/lib/zehos/apps.
        var created = Runtime.Wasm.WasmSaveStore.CreateRoot(new FileService(systemPaths.AdminRoot, pathGuard: CosmosPathGuard.For(systemPaths.AdminRoot), tryBeginWrite: SourceWrites),
            systemPaths.AdminRoot, path.Value!);
        if (!created.IsOK())
        {
            systemLog.Error("Apps", "App-Speicher fuer " + packageId + " nicht angelegt: " + created.Message);
            return null;
        }
        return path.Value;
    }

#endif
    // Nach jedem Einbinden, beim Boot wie bei disk mount: halbe Installationen abschliessen oder entfernen.
    private void ReconcilePackages()
    {
        var reconciled = packages.Reconcile();
        if (!reconciled.IsOK()) systemLog.Error("Packages", reconciled.Message);
    }

    private static string VersionText =>
        $"ZehOS {typeof(Kernel).Assembly.GetName().Version}{ManifestSignature.BuildLabel}; Cosmos {typeof(Sys.Kernel).Assembly.GetName().Version}";

    private ShellService CreateShell(UserSession identity)
    {
        var files = new FileService(identity.HomeDirectory ?? systemPaths.StartDirectory, access: accounts.Access, session: identity,
            diskSpace: CosmosSystemInfo.DiskSpace, directoryEntryLimit: CosmosDirectoryEntryLimit.ForDirectory,
            virtualSource: systemFiles.Produce, isRegularFile: CosmosSystemFiles.IsRegularFile);
        var network = new NetworkShellCommands(networkService, tasks, () => accounts.IsAdmin(identity),
            text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("NET " + text + "\n"), () => identity.Generation);
        var packageCommands = new PackageShellCommands(packages, url => new CosmosHttpsPackageSource(url), path => files.IsVirtualPath(path)
                ? ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument, "cause='VirtualSource' message='Pakete nur aus normalen Verzeichnissen installieren.'")
                : files.ResolvePath(path), tasks,
            () => accounts.IsAdmin(identity), text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("PKG " + text + "\n"), () => identity.Generation);
#if ZEHOS_ROOT_MIGRATION
        var migration = new MigrationShellCommands(new CosmosMigrationService(storage), tasks, () => accounts.IsAdmin(identity),
            () => systemPaths.Profile == PathProfile.Legacy, () => identity.Generation, text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("MIGRATE " + text + "\n"));
#endif
        ShellService? shell = null;
        shell = new ShellService(
        [
            .. new FileShellCommands(files).Commands,
#if ZEHOS_INSTALLER
            // Das Live-Installationsmedium startet ohne Kontendatei in einer lokalen Einrichtungssitzung.
            .. new SetupShellCommands(installer, () => true).Commands,
#else
            .. new UpdateShellCommands(updates, () => accounts.IsAdmin(identity)).Commands,
#endif
            .. new StorageShellCommands(storage, () => SecurityRecovery || accounts.IsAdmin(identity), () => { accounts.SetupBlockedReason = null; accounts.Load(); ReconcilePackages(); systemLog.Information("Storage", "Laufwerksaenderung abgeschlossen."); }, text => systemLog.Information("Storage", "Befehl: " + text)).Commands,
            .. new AccountShellCommands(accounts, identity, files, prepare => shell!.BeginSecretInput(prepare), SecurityRecovery,
                name => shell!.HasCommand(name)).Commands,
            .. network.Commands,
#if ZEHOS_WASM && !ZEHOS_INSTALLER
            // Ohne Wasm-Loader liessen sich Pakete zwar installieren, aber nicht starten.
            .. packageCommands.Commands,
#endif
            .. new TaskShellCommands(tasks, DescribeScheduler).Commands,
            .. new MessageShellCommands(messages).Commands,
            .. new PowerShellCommands(PreparePowerAction, Sys.Power.Reboot, Sys.Power.Shutdown).Commands,
#if ZEHOS_ROOT_MIGRATION
            .. migration.Commands,
#endif
        ], systemLog, GetAvailableUptime,
            VersionText,
            command => accounts.Authorize(identity, command), files: files, getMemory: CosmosSystemInfo.MemoryText, getSessionGeneration: () => identity.Generation);
        shell.TakeJobProgress = packageCommands.TakeProgress;
#if ZEHOS_ROOT_MIGRATION
        shell.TakeJobResult = () => network.TakeResult() ?? packageCommands.TakeResult() ?? migration.TakeResult();
        shell.InterruptJob = migration.Interrupt;
        shell.OnClose = () => { network.Close(); packageCommands.Close(); migration.Close(); accounts.ReleaseSession(identity); };
#else
        shell.TakeJobResult = () => network.TakeResult() ?? packageCommands.TakeResult();
        shell.OnClose = () => { network.Close(); packageCommands.Close(); accounts.ReleaseSession(identity); };
#endif
        return shell;
    }

    protected override void BeforeRun()
    {
        systemLog.Information("Boot", $"ZehOS {CurrentVersion}{ManifestSignature.BuildLabel} startet.");
        var boot = new BootService(systemLog);
        boot.RunStep("Input", () => KeyboardManager.IsEnabled
            ? ResultErr.CreateSuccess()
            : ResultErr.CreateFailure(ResultCode.Failure, "Cosmos-Tastatureingabe ist deaktiviert."));
        bool forceRecovery = false;
#if ZEHOS_RECOVERY
        forceRecovery = true;
#endif
        if (!boot.InitializeDisplay(forceRecovery, InitializeDisplay, RestoreTextConsole))
        {
            systemLog.Error("Boot", "Keine Textkonsole verfuegbar; Diagnose nur seriell.");
            Stop();
            return;
        }

        // Profilwahl (4.3) vor jedem Zugriff auf Konten, Pakete und Updates; ohne ZehOSRootProfile immer Legacy.
#if ZEHOS_ROOT_PROFILE
        if (StorageManager.IsEnabled && StorageManager.IsInitialized)
            layout = CosmosLayoutProfile.Select(text => systemLog.Information("Storage", text));
        InitializeServices(layout?.Decision.Profile == PathProfile.Root ? SystemPaths.Root : SystemPaths.Legacy);
        ApplyLayout();
#else
        InitializeServices(SystemPaths.Legacy);
#endif
        boot.RunStep("Storage", () =>
        {
            if (!StorageManager.IsEnabled || !StorageManager.IsInitialized)
            {
                return ResultErr.CreateFailure(ResultCode.Failure, "Cosmos-Dateidienst ist nicht initialisiert.");
            }

            systemLog.Information("Storage", $"Erkannte Laufwerke: {StorageManager.DeviceCount}.");
            // Gasttests (scripts/test-storage-data-disk.py) lesen Groesse und Einbindung aus dem seriellen Protokoll.
            var listed = storage.GetDevices();
            if (listed.IsOK())
                foreach (var listedDevice in listed.Value!)
                    systemLog.Information("Storage", $"Laufwerk {listedDevice.Name}: {listedDevice.SizeBytes} Bytes, {listedDevice.PartitionCount} Partition(en)");
#if ZEHOS_INSTALLER
            output.WriteLine("ZehOS Setup: setup list, dann setup install <Laufwerk> [--erase].");
            return ResultErr.CreateSuccess();
#else
#if ZEHOS_ROOT_PROFILE
            if (layout?.Decision.Profile == PathProfile.Root)
            {
                // / und /boot/efi sind schon eingehaengt; die Sperre (RootUnclean) kommt allein aus der Profilwahl.
                accounts.SetupBlockedReason = layout.Decision.BlockedReason;
                string text = layout.Decision.Outcome == LayoutOutcome.Root ? "Root-Profil: / (ext2) und /boot/efi eingebunden." : "Root-Profil nur lesend: Root nicht sauber ausgehaengt, offline e2fsck noetig.";
                output.WriteLine(text);
                systemLog.Information("Storage", text);
                ReconcilePackages();
                return ResultErr.CreateSuccess();
            }
#endif
            var mounted = storage.MountData(out string? setupBlockedReason);
#if ZEHOS_ROOT_PROFILE
            setupBlockedReason ??= layout?.Decision.BlockedReason;
#endif
            accounts.SetupBlockedReason = setupBlockedReason;
            if (mounted.IsOK())
            {
                output.WriteLine("Datenlaufwerk unter /data eingebunden.");
                systemLog.Information("Storage", "Datenlaufwerk unter /data eingebunden.");
                ReconcilePackages();
            }
            else
            {
                output.WriteLine(mounted.Message);
                output.WriteLine("Laufwerke: disk list. Leere Platte einrichten: disk init <Name>, einbinden: disk use <Name> --yes (Konten nur beim Boot oder per security recover).");
            }

            return mounted;
#endif
        });
        // In beiden Profilen und unabhaengig von /data (Spec E5b/E6b §0.2); ein Fehler wird nur protokolliert.
        boot.RunStep("VirtualFiles", MountVirtualFiles);
#if !ZEHOS_INSTALLER
        systemLog.Information("Network", $"Cosmos: aktiviert={Cosmos.Kernel.System.Network.NetworkManager.IsEnabled}, initialisiert={Cosmos.Kernel.System.Network.NetworkManager.IsInitialized}, Adapter={Cosmos.Kernel.System.Network.NetworkManager.DeviceCount}, Link={Cosmos.Kernel.System.Network.NetworkManager.LinkUp}.");
        if (Cosmos.Kernel.System.Network.NetworkManager.IsEnabled && Cosmos.Kernel.System.Network.NetworkManager.IsInitialized)
        {
            for (int i = 0; i < Cosmos.Kernel.System.Network.NetworkManager.DeviceCount; i++)
            {
                var adapter = Cosmos.Kernel.System.Network.NetworkManager.GetAdapter(i);
                if (adapter.IsValid) systemLog.Information("Network", $"{adapter.Name}, MAC {adapter.MacAddress}, Link={adapter.LinkUp}.");
            }
        }
        // Ersteinrichtung nur beim Boot: nur dieser Load oeffnet das Fenster, spaetere (disk mount/use/init) bieten kein Konto an.
        accounts.SetupWindowOpen = true;
        var security = accounts.Load();
        accounts.SetupWindowOpen = false;
        if (!security.IsOK() && !accounts.NeedsSetup) output.WriteLine("Konten/Rechte gesperrt. Sicherheits-Recovery erforderlich; vorhandene Dateien werden nicht freigegeben.");
        if (SecurityRecovery) output.WriteLine("Sicherheits-Recovery aktiv: security recover <Adminname> startet die ausdrueckliche Neueinrichtung.");
#endif
        if (boot.IsRecoveryMode)
        {
            output.WriteLine("Wiederherstellungsmodus: Textshell. Diagnose: version, uptime, log.");
        }
        startDesktop = !boot.IsRecoveryMode;
        output.WriteLine("ZehOS Shell - 'help' zeigt die verfuegbaren Befehle.");
        systemLog.Information("Shell", "Textshell ist bereit.");
    }

    protected override void Run()
    {
        InstallScheduler();
#if ZEHOS_WASM && !ZEHOS_INSTALLER
        // Vor dem ersten Netzabruf, solange der 8-MiB-Workerstack noch sicher Platz findet (WasmRuntime.StartWorker).
        Runtime.Wasm.WasmRuntime.StartWorker();
#endif
#if ZEHOS_NETWORK_PROBE
        if (!CosmosNetworkProbe.Run(tasks)) { Stop(); return; }
#endif
#if ZEHOS_THREAD_PROBE
        if (!CosmosThreadProbe.Run())
        {
            Stop();
            return;
        }
#endif
#if ZEHOS_SIMD_PROBE
        if (!CosmosSimdProbe.Run())
        {
            Stop();
            return;
        }
#endif
#if ZEHOS_SECURITY_PROBE
        if (!CosmosSecurityProbe.Run()) { Stop(); return; }
#endif
#if ZEHOS_STORAGE_PROBE
        if (RunStorageProbeAndPowerOff()) return;
#endif
#if ZEHOS_WASM_PROBE
        if (!CosmosWasmProbe.Run()) { Stop(); return; }
#endif
#if ZEHOS_AOT_PROBE
        if (!CosmosAotProbe.Run()) { Stop(); return; }
#endif
#if ZEHOS_INSTALLER
        if (startDesktop)
        {
            // Der grafische Assistent ist der Normalfall; bei einem Anzeigefehler bleibt die Text-Einrichtung mit setup bedienbar.
            try { new InstallerSession(installer).Run(); }
            catch (Exception exception)
            {
                systemLog.Error("Setup", $"Grafische Einrichtung fehlgeschlagen: {exception.Message}");
                RestoreTextConsole();
                output.WriteLine("Grafische Einrichtung nicht verfuegbar. Text-Einrichtung: setup list, setup install <Laufwerk> [--erase].");
            }

            startDesktop = false;
        }
#endif
#if !ZEHOS_INSTALLER
        StartAutomaticNetwork();
#endif
        interactive.Run(startDesktop);
    }

#if ZEHOS_STORAGE_PROBE
    // scripts/test-storage-ext2-guest.py wartet nach STORAGE_PROBE DONE auf das Gastende, auch nach FAIL; der Gast
    // bootet deshalb nie weiter. Als Methode, damit der restliche Run()-Code nicht als unerreichbar gilt (CS0162).
    private bool RunStorageProbeAndPowerOff()
    {
        CosmosExt2GuestProbe.Run();
        var power = PreparePowerAction();
        if (power.IsOK()) Sys.Power.Shutdown();
        Cosmos.Kernel.System.Diagnostics.Log.WriteString($"STORAGE_PROBE shutdown FAIL {power.Message}\n");
        Stop();
        return true;
    }

#endif
    private ResultErr PreparePowerAction()
    {
        if (ShellService.HasRunningPasswordOperations)
            return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperationen laufen noch. Energieaktion nach Abschluss erneut ausfuehren.");

        if (!Sys.Power.IsSupported)
        {
            return ResultErr.CreateFailure(ResultCode.Failure,
                "cause='PowerUnsupported' message='Cosmos unterstuetzt auf diesem System keine Energieaktionen.'");
        }
#if ZEHOS_ROOT_MIGRATION
        // 4.5: StopAll wuerde eine laufende Migration still abbrechen; sie wird ausdruecklich mit system cancel beendet.
        bool stranded = CosmosMigrationService.StrandedReason != null;
        if (AppStorageGate.IsMigrationLocked && !stranded)
            return ResultErr.CreateFailure(ResultCode.Failure, "cause='MigrationRunning' message='Migration laeuft; erst abwarten oder mit system cancel abbrechen.'");
#endif

        automaticNetwork.Stop();
        if (!tasks.StopAll(2000))
        {
#if !ZEHOS_INSTALLER
            StartAutomaticNetwork();
#endif
            return ResultErr.CreateFailure(ResultCode.Failure, "Hintergrundaufgaben laufen noch; Energieaktion abgebrochen.");
        }
        ResultErr unmounted;
#if ZEHOS_ROOT_MIGRATION
        // Nach gescheitertem Zurueckhaengen haengt /data hoechstens ueber den Lese-Wrapper; dort ist nichts zu schreiben, und
        // Unmount() scheiterte an der bewusst gehaltenen Quellsperre.
        if (stranded)
            unmounted = !StorageLock.Run(() => VfsManager.TryGetMount(systemPaths.AdminRoot, out _)) || CosmosStorageService.UnmountData() ? ResultErr.CreateSuccess()
                : ResultErr.CreateFailure(ResultCode.Failure, "cause='UnmountRefused' message='Lesemount von /data liess sich nicht aushaengen.'");
        else
#endif
#if ZEHOS_ROOT_PROFILE
        // Root-Profil: / laesst sich nicht aushaengen; /boot/efi aushaengen, zweimal Sync, s_state = 1 (Spec Abschnitt 2).
        unmounted = layout?.RootVolume is { } rootVolume ? CosmosLayoutProfile.Close(rootVolume)
            : systemPaths.Profile == PathProfile.Legacy && StorageLock.Run(() => VfsManager.TryGetMount(systemPaths.AdminRoot, out _)) ? storage.Unmount() : ResultErr.CreateSuccess();
#else
        unmounted = StorageLock.Run(() => VfsManager.TryGetMount(systemPaths.AdminRoot, out _)) ? storage.Unmount() : ResultErr.CreateSuccess();
#endif
#if !ZEHOS_INSTALLER
        if (!unmounted.IsOK()) StartAutomaticNetwork();
#endif
        return unmounted;
    }

#if !ZEHOS_INSTALLER
    private void StartAutomaticNetwork()
    {
        var started = automaticNetwork.Start();
        if (!started.IsOK()) systemLog.Warning("Network", started.Message);
    }
#endif

#pragma warning disable COSMOS0001
    private void InstallScheduler()
    {
        Cosmos.Kernel.Core.Scheduler.SchedulerManager.SetScheduler(scheduler);
        systemLog.Information("Scheduler", $"{scheduler.Name}; Zeitscheibe={CosmosRoundRobinScheduler.QuantumNs}ns");
    }
#pragma warning restore COSMOS0001

    private string[] DescribeScheduler()
    {
        var snapshot = scheduler.Snapshot();
        return [$"{scheduler.Name}; quantum={CosmosRoundRobinScheduler.QuantumNs}ns; preemptions={snapshot.Preemptions}",
            "Auswahl (aelteste zuerst, letzte128 Thread-IDs): " + string.Join(",", snapshot.ThreadIds)];
    }

    /// <summary>Registriert und haengt zehos-proc, zehos-sys (nur lesbar) und zehos-dev ein; die Baeume sind unabhaengig,
    /// ein Fehler verhindert die anderen nicht und alle Fehler stehen in einer Meldung.</summary>
    private ResultErr MountVirtualFiles()
    {
        var failed = new System.Collections.Generic.List<string>();
        foreach (var (name, root, flags) in new[] { ("zehos-proc", "/proc", MountFlags.ReadOnly), ("zehos-sys", "/sys", MountFlags.ReadOnly), ("zehos-dev", "/dev", MountFlags.None) })
        {
            if (!StorageLock.Run(() => VfsManager.RegisterFileSystem(name, new CosmosVirtualFileSystemType(root, systemFiles.Produce)))) failed.Add(name + " nicht registriert");
            else if (!StorageLock.Run(() => VfsManager.TryMount(name, "", flags, root, out _))) failed.Add(root + " nicht eingehaengt");
        }

        return failed.Count == 0 ? ResultErr.CreateSuccess()
            : ResultErr.CreateFailure(ResultCode.Failure, $"cause='MountFailed' message='{string.Join(", ", failed)}.'");
    }

    private long GetElapsedMilliseconds()
    {
        return clock.ElapsedMilliseconds;
    }

    private long? GetAvailableUptime()
    {
        return TimerManager.IsInitialized ? GetElapsedMilliseconds() : null;
    }

    private static ResultErr InitializeDisplay()
    {
        // Die Textkonsole bleibt als Rueckfall erreichbar; der Desktop startet nach den Bootschritten.
        return RestoreTextConsole();
    }

    private static ResultErr RestoreTextConsole()
    {
        if (!KernelConsole.IsInitialized && !KernelConsole.Initialize())
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Cosmos-Textkonsole konnte nicht initialisiert werden.");
        }

        if (KernelConsole.Default is { } console) console.Font = ProjektZeh.Graphics.CosmosGermanFont.Default;
        Console.Clear();
        return ResultErr.CreateSuccess();
    }
}
