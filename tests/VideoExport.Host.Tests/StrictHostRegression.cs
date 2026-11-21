using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;
using UnityEngine.SceneManagement;

internal static partial class StrictHostRegression
{
    internal static readonly (string Name, Action Run)[] Cases =
    {
        ("actual effects source preparation precedes offline capture and authored frame zero", () =>
        {
            RuntimeModBoundaries.SourcePreparationRequired = true;
            try
            {
                using var fixture = new Fixture();
                Check(RuntimeModBoundaries.SourcePreparations == 1 && RuntimeModBoundaries.SourcePreparedBeforeOffline,
                    "the selected effects archive was not resolved before offline capture");
                Check(fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0, "source discovery executed an authored command");
            }
            finally { RuntimeModBoundaries.SourcePreparationRequired = false; RuntimeModBoundaries.SourcePreparations = RuntimeModBoundaries.SourceRevalidations = 0; }
        }),
        ("replay revalidates the same held effects input without selecting a new identity", () =>
        {
            RuntimeModBoundaries.SourcePreparationRequired = true;
            try
            {
                using var fixture = new Fixture();
                string identity = fixture.Get<string>("_strictIdentity");
                fixture.PrepareOneFrameAndStartReplay();
                Check(RuntimeModBoundaries.SourcePreparations == 1 && RuntimeModBoundaries.SourceRevalidations == 1,
                    "replay omitted original source validation or reacquired a different source input");
                Check(fixture.Get<string>("_strictIdentity") == identity, "replay rebuilt the input identity from runtime state");
            }
            finally { RuntimeModBoundaries.SourcePreparationRequired = false; RuntimeModBoundaries.SourcePreparations = RuntimeModBoundaries.SourceRevalidations = 0; }
        }),
        ("effects source completion cannot absorb a changed frozen native setting", () =>
        {
            RuntimeModBoundaries.SourcePreparationRequired = true;
            RuntimeModBoundaries.BeforeSourcePreparation = () => UserSettings.Instance!.data.masterVolume = .25f;
            try
            {
                using var fixture = new Fixture(begin: false);
                bool rejected = false;
                try { fixture.StartStrict(); }
                catch (TargetInvocationException error) when (error.InnerException is ExportException export && export.Code == "timeline_source_changed") { rejected = true; }
                Check(rejected && !fixture.Host.Capturing && fixture.Captured == 0 && fixture.Player.AdvanceCalls == 0,
                    "source discovery silently refreshed an already frozen native setting or started authored output");
            }
            finally { RuntimeModBoundaries.BeforeSourcePreparation = null; RuntimeModBoundaries.SourcePreparationRequired = false; RuntimeModBoundaries.SourcePreparations = RuntimeModBoundaries.SourceRevalidations = 0; }
        }),
        ("effects input cleanup failure retains file proof and render ownership until shutdown retry", () =>
        {
            var state = typeof(RenderControlV1).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(field => !field.IsLiteral && !field.IsInitOnly).Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
            RuntimeModBoundaries.SourcePreparationRequired = true;
            try
            {
                using var fixture = new Fixture(); fixture.Prime();
                var source = fixture.Get<EffectsSourceInput>("_effectsSourceInput");
                RuntimeModBoundaries.SourceDisposeFailures = 1;
                try
                {
                    fixture.Host.Shutdown();
                    Check(fixture.Host.RetainedCleanupPending && fixture.Get<EffectsSourceInput?>("_effectsSourceInput") != null
                        && RenderControlV1.IsRenderingOwned && !RenderControlV1.IsReady, "input cleanup lost its retry reference or released render ownership");
                    bool denied = false; try { using var write = File.Open(source.ProjectPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                    catch (IOException) { denied = true; }
                    Check(denied, "failed cleanup discarded the retained input file handles");
                    fixture.Host.Shutdown();
                    Check(!fixture.Host.RetainedCleanupPending && fixture.Get<EffectsSourceInput?>("_effectsSourceInput") == null && RuntimeModBoundaries.SourceInput == null,
                        "shutdown retry did not release the held source proof");
                    using var released = File.Open(source.ProjectPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                finally
                {
                    RuntimeModBoundaries.SourceDisposeFailures = 0;
                    if (fixture.Get<EffectsSourceInput?>("_effectsSourceInput") is { } retained)
                    { RuntimeModBoundaries.ReleaseEffectsSourceInput(retained); fixture.Set("_effectsSourceInput", null); }
                }
            }
            finally
            {
                RuntimeModBoundaries.SourcePreparationRequired = false; RuntimeModBoundaries.SourcePreparations = RuntimeModBoundaries.SourceRevalidations = 0;
                foreach (var item in state) item.Field.SetValue(null, item.Value);
            }
        }),
        ("prepared voice and background outputs cannot change the input baseline identity", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            try
            {
                using var fixture = new Fixture();
                string inputIdentity = fixture.Get<string>("_strictIdentity");
                ControlledBackgroundVideoAdapter.Commands = new[] { new ControlledBackgroundCommand(0, 0, "Start", 0, "background.png", new string('a', 64), 0) };
                fixture.PrepareOneFrameAndStartReplay();
                Check(fixture.Get<string>("_strictIdentity") == inputIdentity, "observed output receipts selected a different successful baseline key");
            }
            finally { ControlledBackgroundVideoAdapter.Commands = Array.Empty<ControlledBackgroundCommand>(); }
        }),
        ("same input rejects changed effect timing with identical frame and PCM output", () => WithSharedBaseline(directory =>
        {
            string identity = CompleteBaselineMovie(directory, effectFrame: 0);
            byte[] original = File.ReadAllBytes(Path.Combine(directory, identity.ToLowerInvariant() + ".json"));
            RejectBaselineMovie(directory, effectFrame: 1);
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, identity.ToLowerInvariant() + ".json")))
                && Directory.GetFiles(directory, "*.json").Length == 1, "event mismatch overwrote or forked the successful baseline");
        })),
        ("identical complete repeats retain one input key and one immutable output receipt", () => WithSharedBaseline(directory =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            string first = CompleteBaselineMovie(directory, backgroundFrame: 0, voiceHash: new string('c', 64));
            string path = Path.Combine(directory, first.ToLowerInvariant() + ".json");
            byte[] original = File.ReadAllBytes(path);
            string second = CompleteBaselineMovie(directory, backgroundFrame: 0, voiceHash: new string('c', 64));
            Check(first == second && original.SequenceEqual(File.ReadAllBytes(path)) && Directory.GetFiles(directory, "*.json").Length == 1,
                "identical repeated output changed or duplicated its successful receipt");
        })),
        ("same input rejects changed voice receipt instead of choosing a new baseline", () => WithSharedBaseline(directory =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            CompleteBaselineMovie(directory, voiceHash: new string('c', 64));
            RejectBaselineMovie(directory, voiceHash: new string('d', 64));
            Check(Directory.GetFiles(directory, "*.json").Length == 1, "voice timing change published a separate baseline");
        })),
        ("same input rejects changed background command timing instead of choosing a new baseline", () => WithSharedBaseline(directory =>
        {
            CompleteBaselineMovie(directory, backgroundFrame: 0);
            RejectBaselineMovie(directory, backgroundFrame: 1);
            Check(Directory.GetFiles(directory, "*.json").Length == 1, "background timing change published a separate baseline");
        })),
        ("real source input change permits a different baseline and different output events", () => WithSharedBaseline(directory =>
        {
            string first = CompleteBaselineMovie(directory, effectFrame: 0);
            string second = CompleteBaselineMovie(directory, effectFrame: 1, storyContents: "intentionally changed authored input");
            Check(first != second && Directory.GetFiles(directory, "*.json").Length == 2, "actual input change reused the old baseline");
        })),
        ("legacy row and PCM preflight cannot bypass observed output event proof", () => WithSharedBaseline(directory =>
        {
            string identity = CompleteBaselineMovie(directory);
            var cache = new StrictPreflightCache(directory, identity);
            Check(cache.TryRead(identity, out var receipt, out var files), "fixture did not publish a valid legacy preflight");
            using var fixture = new Fixture(receiptDirectory: directory);
            fixture.Set("_strictPreflightReceipt", receipt); fixture.Set("_strictPreflightFiles", files);
            bool rejected = false;
            try { fixture.Call("BeginStrictPass"); }
            catch (TargetInvocationException error) when (error.InnerException is ExportException export && export.Code == "preflight_cache_invalid") { rejected = true; }
            Check(rejected, "a row/PCM-only cached preflight entered replay without a complete observed output proof");
            Check(File.Exists(files.ManifestPath) && File.Exists(files.PcmPath) && File.Exists(files.TranscriptPath), "refusing incomplete proof removed the old cache");
        })),
        ("missing framework caches reject before recipe acquisition or native story load", () =>
        {
            using var fixture = new Fixture(begin: false); FrameworkGeneratedAssemblyScope.Unavailable = true;
            fixture.Call("Prepare", fixture.Get<ExportOptions>("_options"));
            Check(RuntimeAttributionClient.Acquisitions == 0 && ScenarioResourceManager.Instance!.LoadedNames.Count == 0
                && UnityEngine.Debug.Messages.Any(m => m.Contains("strict_framework_scope_invalid")), "missing framework caches reached recipe/native preparation");
        }),
        ("preparation acquires framework monitors before recipe and capability snapshot", () =>
        {
            using var fixture = new Fixture(begin: false);
            fixture.Call("Prepare", fixture.Get<ExportOptions>("_options"));
            Check(FrameworkGeneratedAssemblyScope.AcquisitionOrder.Take(3).SequenceEqual(new[] { "framework", "recipe", "inventory" }),
                "framework proof was absent or acquired after recipe/capability snapshot");
        }),
        ("cancelled final worker cannot retain a usable framework or recipe proof", () =>
        {
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            var held = fixture.Get<FrameworkGeneratedAssemblyScope>("_frameworkScope");
            var writer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Get<FramePipeline>("_frames").PendingWriter = writer.Task;
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            var work = fixture.Get<Task<ExportResult>>("_finalize");
            fixture.Call("Cancel");
            bool invalid = false; try { held.Validate(); } catch (ExportException) { invalid = true; }
            writer.SetResult(true); fixture.Cleanup();
            Check(invalid && !fixture.Session.Completed && RuntimeAttributionClient.ActiveTokens == 0 && FrameworkGeneratedAssemblyScope.Releases == 1
                && work.IsCompleted, "cancellation left final worker with usable proof or leaked the held object");
        }),
        ("framework mutation before pass seal cannot create replay proof", () =>
        {
            using var fixture = new Fixture(); fixture.Prime(); fixture.Frame(1600); fixture.Host.BeforePlayerEnd(fixture.Player);
            FrameworkGeneratedAssemblyScope.Invalidated = true;
            bool rejected = false; try { fixture.Call("FinishStrictPass"); } catch (TargetInvocationException error) when (error.InnerException is ExportException) { rejected = true; }
            Check(rejected && !fixture.Get<bool>("_strictReplayPending"), "pass seal accepted changed framework state");
        }),
        ("missing framework scope rejects the next strict frame without reacquisition", () =>
        {
            using var fixture = new Fixture(); fixture.Prime();
            var field = typeof(ExportHost).GetField("_frameworkScope", BindingFlags.Instance | BindingFlags.NonPublic);
            (field?.GetValue(fixture.Host) as IDisposable)?.Dispose(); field?.SetValue(fixture.Host, null);
            fixture.Frame(1600); fixture.Cleanup();
            Check(fixture.Captured == 0 && fixture.Session.Cancelled, "strict frame bypassed missing held framework proof");
        }),
        ("late framework change cancels the next frame and cleanup releases the held object", () =>
        {
            using var fixture = new Fixture(); fixture.Prime(); FrameworkGeneratedAssemblyScope.Invalidated = true;
            fixture.Frame(1600); FrameworkGeneratedAssemblyScope.Invalidated = false; fixture.Cleanup();
            Check(fixture.Captured == 0 && fixture.Session.Cancelled && FrameworkGeneratedAssemblyScope.Releases == 1,
                "late framework mutation was accepted or held object was leaked");
        }),
        ("one framework object spans replay and publishes between controllers and recipe", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture();
            var field = typeof(ExportHost).GetField("_frameworkScope", BindingFlags.Instance | BindingFlags.NonPublic);
            var held = field?.GetValue(fixture.Host);
            fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            Check(held != null && ReferenceEquals(held, field!.GetValue(fixture.Host)) && FrameworkGeneratedAssemblyScope.Acquisitions == 1,
                "framework proof did not retain the same object through replay");
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            fixture.Get<Task<ExportResult>>("_finalize").GetAwaiter().GetResult(); fixture.Host.Update();
            var order = RuntimeAttributionClient.CommitOrder;
            Check(fixture.Session.Completed && FrameworkGeneratedAssemblyScope.Enters == 1 && FrameworkGeneratedAssemblyScope.Releases == 1
                && order.IndexOf("voice") < order.IndexOf("framework") && order.IndexOf("framework") < order.IndexOf("recipe"),
                "framework publication guard was missing, reordered, or leaked");
            Check(FrameworkGeneratedAssemblyScope.ExitOrder.SequenceEqual(new[] { "recipe", "framework" }), "publication guards did not release recipe before framework");
        }),
        ("framework commit refusal cancels publication and releases recipe and framework", () =>
        {
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            FrameworkGeneratedAssemblyScope.RefuseCommit = true;
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            bool rejected = false; try { fixture.Get<Task<ExportResult>>("_finalize").GetAwaiter().GetResult(); } catch (ExportException) { rejected = true; }
            fixture.Host.Update(); fixture.Cleanup();
            Check(rejected && !fixture.Session.Completed && RuntimeAttributionClient.ActiveTokens == 0 && FrameworkGeneratedAssemblyScope.Releases == 1,
                "failed framework commit published or leaked held proofs");
        }),
        ("recipe publication refusal releases its token and all retained controllers", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            RuntimeAttributionClient.RefuseCommit = true;
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            var work = fixture.Get<Task<ExportResult>>("_finalize");
            bool rejected = false; try { work.GetAwaiter().GetResult(); } catch (ExportException) { rejected = true; }
            fixture.Host.Update(); fixture.Cleanup();
            Check(rejected && !fixture.Session.Completed && RuntimeAttributionClient.ActiveTokens == 0 && RuntimeAttributionClient.Releases == 1
                && !fixture.Host.RetainedCleanupPending, "publication refusal leaked a recipe token/controller or published output");
        }),
        ("one collecting recipe scope spans both passes and enters publication last", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture();
            var scope = fixture.Get<RetainedRecipeScope>("_recipeScope");
            fixture.PrepareOneFrameAndStartReplay(); RuntimeAttributionClient.RecipesHash = new string('c',64); fixture.Prime(); fixture.Frame(1600);
            Check(ReferenceEquals(scope, fixture.Get<RetainedRecipeScope>("_recipeScope")) && RuntimeAttributionClient.Acquisitions == 1 && RuntimeAttributionClient.Seals == 0,
                "collecting recipe scope was replaced or sealed before final playback ended");
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            fixture.Get<Task<ExportResult>>("_finalize").GetAwaiter().GetResult(); fixture.Host.Update();
            Check(fixture.Session.Completed && RuntimeAttributionClient.Seals == 1 && RuntimeAttributionClient.Enters == 1
                && RuntimeAttributionClient.CommitOrder.Last() == "recipe" && RuntimeAttributionClient.CommitOrder.IndexOf("voice") < RuntimeAttributionClient.CommitOrder.IndexOf("recipe")
                && fixture.Session.RuntimeFactoryRecipe?.RecipesSha256 == new string('c',64), "recipe guard/terminal receipt did not close the actual final publication chain");
            Check(RuntimeAttributionClient.Releases == 1, "finished recipe token was not released exactly once by its owner");
        }),
        ("missing recipe scope rejects the next strict frame without reacquisition", () =>
        {
            using var fixture = new Fixture(); fixture.Prime(); fixture.Get<RetainedRecipeScope>("_recipeScope").Dispose(); fixture.Set("_recipeScope",null);
            fixture.Frame(1600); fixture.Cleanup();
            Check(fixture.Captured == 0 && fixture.Session.Cancelled && RuntimeAttributionClient.Acquisitions == 1, "strict frame bypassed missing recipe proof or reacquired it");
        }),
        ("late invalid recipe emission cancels capture and a later valid response cannot heal it", () =>
        {
            using var fixture = new Fixture(); fixture.Prime(); RuntimeAttributionClient.Invalidated = true;
            fixture.Frame(1600); RuntimeAttributionClient.Invalidated = false; fixture.Cleanup();
            Check(fixture.Captured == 0 && fixture.Session.Cancelled && RuntimeAttributionClient.Acquisitions == 1 && RuntimeAttributionClient.Releases == 1,
                "late emission was accepted, hidden by new scope, or leaked the token");
        }),
        ("menu readiness resumes automatically without recreating retained scopes", () =>
        {
            using var fixture = new Fixture(begin: false); RetainedMenuPlaybackBoundary.Ready = false;
            fixture.Call("Prepare", fixture.Get<ExportOptions>("_options")); fixture.Host.Update();
            RetainedMenuPlaybackBoundary.Ready = true; fixture.Host.Update();
            Check(fixture.Get<ExportOptions?>("_retainedPreparationRequest") == null && RetainedMenuPlaybackBoundary.Acquisitions == 1
                && ScenarioResourceManager.Instance!.LoadedNames.Count == 1 && fixture.Get<Task<ExportSession>?>("_prepare") != null,
                "ready menu audio did not continue source preparation exactly once");
        }),
        ("voice warmup cancellation retains no offline scope or controller", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true; CharacterVoiceSessionAdapter.PreloadReady = false;
            using var fixture = new Fixture(); fixture.Call("Cancel"); fixture.Cleanup();
            Check(!fixture.Host.Capturing && !fixture.Get<bool>("_voiceWarmup") && CharacterVoiceSessionAdapter.Disposals == 1
                && !RetainedMenuPlaybackBoundary.IsHeld && !RenderControlV1.IsRenderingOwned && RuntimeAttributionClient.ActiveTokens == 0
                && RuntimeAttributionClient.Releases == 1, "cancelled native preload leaked a scope or render ownership");
        }),
        ("retained voice cleanup failure keeps retry reference and prevents render release", () =>
        {
            var state = typeof(RenderControlV1).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(field => !field.IsLiteral && !field.IsInitOnly).Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
            try
            {
                CharacterVoiceSessionAdapter.Loaded = true;
                using var fixture = new Fixture(); fixture.Prime(); CharacterVoiceSessionAdapter.DisposeFailures = 1;
                fixture.Host.Shutdown();
                Check(fixture.Host.RetainedCleanupPending && fixture.Get<CharacterVoiceSessionAdapter?>("_voiceSession") != null
                    && RenderControlV1.IsRenderingOwned && !RenderControlV1.IsReady, "failed cleanup lost reference or released render coordination");
                fixture.Host.Shutdown();
                Check(!fixture.Host.RetainedCleanupPending && CharacterVoiceSessionAdapter.Disposals == 1, "subsequent cleanup did not retry the held controller");
            }
            finally { foreach (var item in state) item.Field.SetValue(null, item.Value); }
        }),
        ("late voice controller mutation rejects the next frame without reattachment", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture(); fixture.Prime(); CharacterVoiceSessionAdapter.Invalidated = true;
            fixture.Frame(1600); fixture.Cleanup();
            Check(fixture.Captured == 0 && fixture.Session.Cancelled && CharacterVoiceSessionAdapter.Attaches == 1,
                "late voice proof mutation was accepted or hidden by a fresh controller");
        }),
        ("queued retained callback fault blocks the next frame before safe cleanup", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture(); fixture.Prime();
            fixture.Host.RetainedBoundaryFailed(new InvalidOperationException("nested retained callback fault"));
            Check(!fixture.Session.Cancelled && CharacterVoiceSessionAdapter.Disposals == 0,
                "callback synchronously disposed another controller inside the originating guard stack");
            fixture.Frame(1600); fixture.Cleanup();
            Check(fixture.Captured == 0 && fixture.Session.Cancelled && CharacterVoiceSessionAdapter.Disposals == 1,
                "queued retained fault accepted a frame or failed safe-boundary cleanup");
        }),
        ("late capability refresh uses the same held voice controller", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture(); fixture.Prime();
            fixture.Set("_strictCapabilitySnapshot", StrictCapabilityInventory.Capture(fixture.Get<StaticInertScope>("_staticInertScope"), fixture.Get<CharacterVoiceSessionAdapter>("_voiceSession")));
            fixture.Set("_strictCapabilityGeneration", StrictCapabilityInventory.AssemblyGeneration);
            StrictCapabilityInventory.AssemblyGeneration++;
            fixture.Host.Update();
            Check(!fixture.Session.Cancelled && CharacterVoiceSessionAdapter.Attaches == 1, "capability refresh dropped or recreated the voice scope");
        }),
        ("retained menu restoration precedes native snapshots and one scope spans both passes", () =>
        {
            RetainedMenuPlaybackBoundary.RequireBeforeNative = true;
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay();
            Check(!RetainedMenuPlaybackBoundary.ConstructedBeforeMenu && RetainedMenuPlaybackBoundary.Acquisitions == 1 && RetainedMenuPlaybackBoundary.Begins == 2 && RetainedMenuPlaybackBoundary.Ends == 1,
                "menu proof was recreated or its original scene exit was not repeated per pass");
        }),
        ("pending menu audio readiness waits automatically before story read and is cancellable", () =>
        {
            using var fixture = new Fixture(begin: false); RetainedMenuPlaybackBoundary.Ready = false;
            fixture.Call("Prepare", fixture.Get<ExportOptions>("_options")); fixture.Host.Update();
            Check(fixture.Get<ExportOptions?>("_retainedPreparationRequest") != null && ScenarioResourceManager.Instance!.LoadedNames.Count == 0,
                "menu preload waiting did not hold preparation before story reads");
            fixture.Call("Cancel"); fixture.Cleanup();
            Check(fixture.Get<ExportOptions?>("_retainedPreparationRequest") == null && !RetainedMenuPlaybackBoundary.IsHeld,
                "cancellation retained menu waiting state");
        }),
        ("voice preload keeps native clocks live and submits no frame until ready", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true; CharacterVoiceSessionAdapter.PreloadReady = false;
            using var fixture = new Fixture();
            fixture.Host.Update();
            Check(!fixture.Host.Capturing && fixture.Captured == 0 && fixture.Player.AdvanceCalls == 0
                && fixture.Get<NativeCaptureScope?>("_native") == null && CharacterVoiceSessionAdapter.Arms == 0,
                "voice waiting created an offline clock, advanced native story or captured content");
            CharacterVoiceSessionAdapter.PreloadReady = true; fixture.Host.Update();
            Check(fixture.Host.Capturing && CharacterVoiceSessionAdapter.Prepares == 1 && CharacterVoiceSessionAdapter.Arms == 0,
                "ready native preload did not start the normal mixer primer exactly once");
            fixture.Prime();
            Check(CharacterVoiceSessionAdapter.Arms == 1 && CharacterVoiceSessionAdapter.Last!.Position!().SampleFrame == 0,
                "voice was not armed at authored zero after PCM timeline reset");
        }),
        ("voice prepared receipt is retained for replay and commit waits for both passes", () =>
        {
            CharacterVoiceSessionAdapter.Loaded = true;
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime();
            Check(CharacterVoiceSessionAdapter.Attaches == 1 && CharacterVoiceSessionAdapter.Binds == 1
                && ReferenceEquals(CharacterVoiceSessionAdapter.Last!.Expected, CharacterVoiceSessionAdapter.Last.Prepared),
                "voice scope/story was recreated or replay did not use its own preparation receipt");
            fixture.Frame(1600); fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            fixture.Get<Task<ExportResult>>("_finalize").GetAwaiter().GetResult(); fixture.Host.Update();
            Check(fixture.Session.Completed && CharacterVoiceSessionAdapter.Seals == 2 && CharacterVoiceSessionAdapter.CommitSeals == 1
                && CharacterVoiceSessionAdapter.Disposals == 1, "voice receipt/commit/disposal lifecycle was incomplete");
        }),
        ("static inert receipt stays identical across preparation replay and frames", () =>
        {
            using var fixture=new Fixture(staticInertPresent:true);
            var first=fixture.InertScope;int images=StaticInertModEvidence.Images;
            fixture.Prime();fixture.Frame(1600);fixture.FinishPreparationAndStartReplay();
            Check(ReferenceEquals(first,fixture.InertScope) && StaticInertModEvidence.Creations==1,"Scene reload replaced the retained receipt");
            Check(StaticInertModEvidence.Frames>2 && StaticInertModEvidence.Images==images,"Frames skipped validation or reread payload images");
        }),
        ("missing static inert lease cancels before a strict frame", () =>
        {
            using var fixture=new Fixture(staticInertPresent:true);var scope=fixture.InertScope;
            fixture.Set("_staticInertScope",null);fixture.Frame(0);
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing,"Strict frame accepted a missing inert scope");
            scope.Dispose();fixture.Cleanup();
        }),
        ("late static inert failure stays latched and blocks replay without reacquisition", () =>
        {
            using var fixture=new Fixture(staticInertPresent:true);var scope=fixture.InertScope;
            fixture.Prime();fixture.Frame(1600);StaticInertModEvidence.Invalidated=true;
            fixture.Host.BeforePlayerEnd(fixture.Player);fixture.Host.Update();fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Get<bool>("_strictReplayPending") && StaticInertModEvidence.Creations==1,"Preparation seal replaced or ignored a failed receipt");
            StaticInertModEvidence.Invalidated=false;bool stale=false;
            try{scope.ValidateFrame();}catch(ExportException){stale=true;}
            Check(stale,"Old export scope revived after failure repair");
        }),
        ("worker static inert commit revalidation stops final publication", () =>
        {
            using var fixture=new Fixture(staticInertPresent:true);fixture.PrepareOneFrameAndStartReplay();fixture.Prime();fixture.Frame(1600);
            fixture.Session.BeforeOutputCommit=()=>StaticInertModEvidence.RejectCommit=true;
            fixture.Host.BeforePlayerEnd(fixture.Player);fixture.Host.Update();
            var work=fixture.Get<Task<ExportResult>?>("_finalize");Check(work!=null,"Fixture never reached finalizer");bool rejected=false;
            try{work!.GetAwaiter().GetResult();}catch(ExportException error) when(error.Code=="strict_static_inert_invalid"){rejected=true;}
            Check(rejected && !fixture.Session.Completed,"Commit published with invalid image/receipt proof");
            fixture.Host.Update();StaticInertModEvidence.RejectCommit=false;
        }),
        ("static inert cancellation releases the old scope and permits a clean retry", () =>
        {
            StaticInertScope old;
            using(var fixture=new Fixture(staticInertPresent:true))
            {
                old=fixture.InertScope;fixture.Prime();fixture.Call("Cancel");fixture.Cleanup();
                Check(fixture.Get<StaticInertScope?>("_staticInertScope")==null,"Cancelled scope remained attached");
            }
            bool stale=false;try{old.ValidateFrame();}catch(ExportException){stale=true;}Check(stale,"Cancelled receipt could still authorize work");
            using var retry=new Fixture(staticInertPresent:true);retry.InertScope.ValidateFrame();
            Check(!ReferenceEquals(old,retry.InertScope) && StaticInertModEvidence.Creations==2,"Retry did not acquire a distinct clean scope");
        }),
        ("translation and dependency scopes follow both passes and pump before native rendering", () =>
        {
            using var fixture = new Fixture();
            Check(MultilingualCaptureAdapter.Active && RuntimeModBoundaries.Active, "controlled translation/dependency scope was not acquired");
            fixture.Prime(); fixture.Host.BeforeFrameRender();
            Check(MultilingualCaptureAdapter.Pumps.LastOrDefault(-1) == 0, "pre-render callback did not pump current translation frame");
            fixture.Frame(1600); fixture.FinishPreparationAndStartReplay();
            Check(MultilingualCaptureAdapter.Begins == 2 && MultilingualCaptureAdapter.Ends == 1 && RuntimeModBoundaries.Active,
                "strict scene roundtrip did not restore and reacquire scoped adapters");
            fixture.Prime(); fixture.Call("Cancel"); fixture.Cleanup();
            Check(!MultilingualCaptureAdapter.Active && !RuntimeModBoundaries.Active && !RenderControlV1.IsRenderingOwned,
                "cancellation released rendering before controlled translation/dependency scopes restored");
        }),
        ("background restore failure cancels publication and retains actual render ownership", () =>
        {
            // This is a terminal production fault until restart. Save/restore
            // only test-process statics so later independent cases still run.
            var state = typeof(RenderControlV1).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(field => !field.IsLiteral && !field.IsInitOnly)
                .Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
            try
            {
                using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
                int releases = 0;
                Action after = () => releases++;
                RenderControlV1.AfterRelease += after;
                try
                {
                    ControlledBackgroundVideoAdapter.EndFailure = new InvalidOperationException("fixture native background restore failed");
                    fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update(); fixture.Cleanup();
                    Check(fixture.Session.Cancelled && !fixture.Session.Completed && fixture.Get<Task<ExportResult>?>("_finalize") == null,
                        "background restoration failure allowed final publication");
                    Check(RenderControlV1.IsRenderingOwned && !RenderControlV1.IsReady && releases == 0,
                        "unrestored native capture released real render ownership");
                }
                finally { RenderControlV1.AfterRelease -= after; ControlledBackgroundVideoAdapter.EndFailure = null; }
            }
            finally { foreach (var item in state) item.Field.SetValue(null, item.Value); }
        }),
        ("background source changes cannot commit even with identical frame and PCM counts", () =>
        {
            try
            {
                using var fixture = new Fixture();
                fixture.Prime();
                var command = new ControlledBackgroundCommand(0, 0, "Start", 0, "background.png", new string('a', 64), 0);
                ControlledBackgroundVideoAdapter.Commands = new[] { command };
                fixture.Frame(1600);
                fixture.FinishPreparationAndStartReplay();
                fixture.Prime();
                ControlledBackgroundVideoAdapter.Commands = new[] { command with { SourceSha256 = new string('b', 64) } };
                fixture.Frame(1600);
                fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update(); fixture.Cleanup();
                Check(fixture.Session.Cancelled && !fixture.Session.Completed, "Changed background source committed output.");
                Check(UnityEngine.Debug.Messages.Any(m => m.Contains("background_replay_mismatch")), "Missing background source mismatch diagnostic.");
            }
            finally { ControlledBackgroundVideoAdapter.Commands = Array.Empty<ControlledBackgroundCommand>(); }
        }),
        ("voice authored delay follows committed frames and reacquires on strict replay", () =>
        {
            using var fixture = new Fixture();
            Check(CharacterVoiceDelayClock.Active && CharacterVoiceDelayClock.Begins == 1
                && CharacterVoiceDelayClock.Fps == 30 && CharacterVoiceDelayClock.FrameIndex?.Invoke() == 0,
                "voice delay was not acquired with the committed frame clock");
            fixture.Prime(); fixture.Frame(1600);
            Check(CharacterVoiceDelayClock.FrameIndex?.Invoke() == 1, "voice delay did not follow the committed frame");
            fixture.FinishPreparationAndStartReplay();
            Check(CharacterVoiceDelayClock.Begins == 2 && CharacterVoiceDelayClock.Ends == 1
                && CharacterVoiceDelayClock.FrameIndex?.Invoke() == 0,
                "strict scene replay retained the previous voice clock or missed reacquisition");
        }),
        ("voice late-load discovery runs on host updates", () =>
        {
            using var fixture = new Fixture();
            int before = CharacterVoiceDelayClock.InstallAttempts;
            fixture.Host.Update();
            Check(CharacterVoiceDelayClock.InstallAttempts > before, "host update never installs a late-loaded voice adapter");
        }),
        ("voice clock fault cancels the active encoder synchronously", () =>
        {
            using var fixture = new Fixture(); fixture.Prime();
            fixture.Call("CharacterVoiceClockFailed", new InvalidOperationException("fixture voice clock fault"));
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing && !CharacterVoiceDelayClock.Active,
                "voice clock fault did not synchronously cancel and restore capture");
            fixture.Cleanup();
        }),
        ("voice delay is restored before the final render ownership handoff", () =>
        {
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            bool released = false, voiceRestoredAtRelease = false;
            Action after = () => { voiceRestoredAtRelease = !CharacterVoiceDelayClock.Active && CharacterVoiceDelayClock.Ends == 2; released = true; };
            RenderControlV1.AfterRelease += after;
            try
            {
                fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
                fixture.Get<Task<ExportResult>>("_finalize").GetAwaiter().GetResult(); fixture.Host.Update();
                Check(released && voiceRestoredAtRelease && fixture.Session.Completed,
                    "successful strict replay released before voice restoration or did not complete");
            }
            finally { RenderControlV1.AfterRelease -= after; }
        }),
        ("voice restoration callback fault stops publication after capture has become inert", () =>
        {
            using var fixture = new Fixture(); fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            CharacterVoiceDelayClock.OnEnd = () => fixture.Call("CharacterVoiceClockFailed", new InvalidOperationException("fixture restoration clock fault"));
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Session.Completed && fixture.Get<Task<ExportResult>?>("_finalize") == null,
                "voice fault during inert restoration was ignored and allowed final publication");
            CharacterVoiceDelayClock.OnEnd = null;
            fixture.Cleanup();
        }),
        ("startup boundary remains owned across both passes and releases after completion", () =>
        {
            using var fixture = new Fixture();
            fixture.AttachStartupBoundary();
            fixture.PrepareOneFrameAndStartReplay();
            Check(AASkipIntroExportBoundary.IsActive && AASkipIntroExportBoundary.Disposes == 0,
                "scene roundtrip disposed the retained startup lease");
            fixture.Prime(); fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "matching replay did not finalize");
            finalize!.GetAwaiter().GetResult(); fixture.Host.Update();
            Check(fixture.Session.Completed && AASkipIntroExportBoundary.CommitValidations == 1,
                "publication did not validate the retained controller");
            Check(!AASkipIntroExportBoundary.IsActive && AASkipIntroExportBoundary.Disposes == 1,
                "completed export leaked or double-disposed its startup lease");
        }),
        ("startup invalidation prevents accepting the next captured frame", () =>
        {
            using var fixture = new Fixture(); fixture.AttachStartupBoundary();
            fixture.PrepareOneFrameAndStartReplay(); fixture.Prime();
            long captured = fixture.Captured;
            AASkipIntroExportBoundary.Invalidated = true;
            fixture.Frame(1600);
            Check(fixture.Captured == captured && fixture.Session.FramesWritten == 0 && fixture.Session.Cancelled,
                "invalid startup boundary still accepted a video frame");
            fixture.Cleanup();
            Check(fixture.Get<Task?>("_cleanup") == null && !AASkipIntroExportBoundary.IsActive && !RenderControlV1.IsRenderingOwned,
                "latched startup fault prevented cleanup from completing and releasing ownership");
        }),
        ("an initially absent startup Mod is checked when it appears during the same export", () =>
        {
            using var fixture = new Fixture(); fixture.AttachStartupBoundary(present: false);
            fixture.Prime();
            AASkipIntroExportBoundary.Present = true;
            fixture.Frame(1600);
            Check(fixture.Session.Cancelled && fixture.Captured == 0,
                "null lease bypassed validation of the initially absent Mod");
            fixture.Cleanup();
            Check(fixture.Get<Task?>("_cleanup") == null && !RenderControlV1.IsRenderingOwned,
                "late startup load repeatedly faulted before cancelled cleanup could complete");
        }),
        ("startup worker commit check can prevent publication after rendering", () =>
        {
            using var fixture = new Fixture(); fixture.AttachStartupBoundary();
            fixture.PrepareOneFrameAndStartReplay(); fixture.Prime(); fixture.Frame(1600);
            AASkipIntroExportBoundary.RejectCommit = true;
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "fixture never entered worker finalization");
            bool rejected = false;
            try { finalize!.GetAwaiter().GetResult(); }
            catch (ExportException error) when (error.Code == "strict_startup_boundary_invalid") { rejected = true; }
            Check(rejected && !fixture.Session.Completed, "worker published without the retained boundary check");
            fixture.Host.Update();
        }),
        ("strict startup primes the mixer before any authored scenario event", () =>
        {
            using var fixture = new Fixture();
            Check(fixture.Player.AdvanceCalls == 0, "initial native advance was not held");
            fixture.Frame(0);
            fixture.Host.Update();
            Check(fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0, "empty primer advanced story time");
            fixture.Frame(1600);
            Check(fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0, "positive primer executed the script inside the render callback");
            fixture.Host.Update();
            Check(fixture.Player.AdvanceCalls == 1 && fixture.Captured == 0, "first authored event did not resume exactly once on Update");
            fixture.Host.Update();
            Check(fixture.Player.AdvanceCalls == 1, "subsequent Update repeated the first authored event");
            Check(fixture.Session.FramesWritten == 0 && fixture.Session.AudioSampleFrames == 0,
                "primer produced output video or output PCM");
        }),
        ("44.1 kHz at 24 fps starts only after a complete 1837 plus 1838 sample primer cycle", () =>
        {
            using var fixture = new Fixture(44100, 24);
            fixture.Frame(0);
            fixture.Frame(1837);
            fixture.Host.Update();
            Check(fixture.Player.AdvanceCalls == 0 && fixture.Captured == 0 && fixture.Host.UiClock == 0,
                "an incomplete fractional-sample cycle released an authored event or advanced the logical clock");
            fixture.Frame(1838);
            Check(fixture.Player.AdvanceCalls == 0, "completed primer replayed the script inside the render callback");
            fixture.Host.Update();
            Check(fixture.Player.AdvanceCalls == 1 && fixture.Captured == 0 && fixture.Host.UiClock == 0,
                "complete rational cycle did not resume the initial event at logical time zero");
            fixture.Frame(1837);
            fixture.Frame(1838);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            var plan = fixture.Get<StrictFramePlan>("_strictPlan");
            Check(plan.FrameCount == 2 && plan.AudioSampleFrameCount == 3675,
                "primer phase contaminated the authored timeline sample budget");
        }),
        ("strict preparation records PCM but encodes no video frames", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Frame(1600);
            fixture.Frame(1600);
            Check(fixture.Captured == 2, "preparation timeline did not advance");
            Check(fixture.Session.FramesWritten == 0 && fixture.Session.AudioSampleFrames == 0,
                "preparation leaked into final encoder input");
            Check(fixture.Get<RawAudioPass>("_rawAudio").RawSampleFrames == 3200,
                "raw PCM was not retained exactly before the end fence was sealed");
        }),
        ("strict End before and after the same render seal identical one-frame plans", () =>
        {
            foreach (bool endAfter in new[] { false, true })
            {
                using var fixture = new Fixture();
                fixture.Prime();
                UnityEngine.Time.frameCount++;
                if (!endAfter) Check(!fixture.Host.BeforePlayerEnd(fixture.Player), "pre-render End escaped exporter fence");
                fixture.CaptureCurrent(1600);
                if (endAfter) Check(!fixture.Host.BeforePlayerEnd(fixture.Player), "post-render End escaped exporter fence");
                fixture.Host.Update();
                Check(fixture.Get<StrictFramePlan>("_strictPlan").FrameCount == 1,
                    "end callback ordering added or omitted a boundary frame");
                Check(fixture.Get<bool>("_strictReplayPending") && fixture.Player.EndCalls == 1,
                    "preparation did not return once for video replay");
                Check(RenderControlV1.IsRenderingOwned, "scene roundtrip released the outer rendering lease");
                Check(fixture.Session.FramesWritten == 0, "final boundary frame was encoded during preparation");
            }
        }),
        ("strict duplicate End cannot extend the preparation fence", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            UnityEngine.Time.frameCount++;
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.CaptureCurrent(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            Check(fixture.Get<StrictFramePlan>("_strictPlan").FrameCount == 1, "repeated native End extended duration");
        }),
        ("strict preparation waits for one native tail sample without padding", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Frame(1599);
            NativeCaptureScope.AudioSampleCounts.Enqueue(1);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            Check(fixture.Captured == 1 && fixture.Get<bool>("_strictReplayPending") && !fixture.Session.Completed,
                "native tail sample was not drained to the fixed content boundary");
            Check(!UnityEngine.Debug.Messages.Any(m => m.Contains("audio_timeline_incomplete")), "available native tail was rejected as a shortage");
        }),
        ("strict preparation retains one native tail sample without changing video length", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Frame(1601);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            Check(fixture.Captured == 1 && fixture.Get<bool>("_strictReplayPending") && !fixture.Session.Completed,
                "native tail sample changed the fixed video length or prevented preparation");
            Check(!UnityEngine.Debug.Messages.Any(m => m.Contains("audio_timeline_extra")), "native tail sample was misclassified as excess content");
        }),
        ("strict scene roundtrip replays fixed PCM and commits only matching native End", () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareOneFrameAndStartReplay();
            Check(fixture.Session.BoundPlan?.FrameCount == 1, "video pass has no fixed preparation contract");
            fixture.Prime();
            fixture.Frame(1600);
            Check(fixture.Session.FramesWritten == 1 && fixture.Session.AudioSampleFrames == 1600,
                "replay failed to write fixed video and prepared audio");
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "matching replay did not enter finalization: " + string.Join(" | ", UnityEngine.Debug.Messages.TakeLast(4)));
            finalize!.GetAwaiter().GetResult();
            fixture.Host.Update();
            Check(fixture.Session.Completed && !fixture.Host.Capturing, "matching replay was not completed");
            Check(!RenderControlV1.IsRenderingOwned, "completed two-pass export retained render ownership");
        }),
        ("strict replay waits for source check without losing its catalog continuation", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            string identity = fixture.WaitReplaySourceCheck();
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.SetReplaySourceCheck(pending.Task);
            fixture.Host.Update();
            fixture.Host.Update();
            Check(!fixture.Get<bool>("_armed") && fixture.Get<bool>("_strictReplayPending") && RenderControlV1.IsRenderingOwned,
                "incomplete source check armed replay or lost the retained ownership");
            pending.SetResult(identity);
            fixture.Host.Update();
            Check(fixture.Get<bool>("_armed") && !fixture.Get<bool>("_strictReplayPending"),
                "completed source check never resumed after the catalog-return flag was cleared");
        }),
        ("strict replay cannot publish when a prepared command is missing after scene reload", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Host.RecordStrictSandboxCommand("fixture-audio", "Play", "logical-source-1", new string('b', 64));
            fixture.Frame(1600);
            fixture.FinishPreparationAndStartReplay();
            fixture.Prime();
            fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Session.Completed,
                "scene reload discarded command validation and accepted the missing event");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("sandbox_replay_divergence")),
                "missing replay event did not produce a command divergence diagnostic");
        }),
        ("strict matching command replay survives the scene roundtrip", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Host.RecordStrictSandboxCommand("fixture-audio", "Play", "logical-source-1", new string('b', 64));
            fixture.Frame(1600);
            fixture.FinishPreparationAndStartReplay();
            fixture.Prime();
            fixture.Host.RecordStrictSandboxCommand("fixture-audio", "Play", "logical-source-1", new string('b', 64));
            fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "matching command replay could not finalize");
            finalize!.GetAwaiter().GetResult();
            fixture.Host.Update();
            Check(fixture.Session.Completed, "matching command replay never committed output");
        }),
        ("strict second pass still checks a newly loaded module", () =>
        {
            using var fixture = new Fixture();
            fixture.Set("_strictCapabilitySnapshot", StrictCapabilityInventory.Snapshot);
            fixture.PrepareOneFrameAndStartReplay();
            StrictCapabilityInventory.Snapshot = StrictCapabilitySnapshot.Create(new[] {
                new StrictCapability("Late.SecondPass", new string('c', 64), StrictCapabilityLevel.Opaque, new[] { "new-on-replay" })
            });
            StrictCapabilityInventory.AssemblyGeneration++;
            fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Host.Capturing,
                "second pass lost the capability snapshot and ignored the newly loaded module");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("Late.SecondPass")),
                "second-pass module identity was not reported");
        }),
        ("strict cancellation releases command state before a retry", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Host.RecordStrictSandboxCommand("fixture-audio", "Play", "logical-source-1", new string('b', 64));
            fixture.Call("Cancel");
            fixture.Cleanup();
            fixture.Call("InitializeStrictPreparation", fixture.Story);
            Check(fixture.Get<StrictSandboxTrace?>("_strictSandboxTrace") == null,
                "cancelled export left its commands attached to the next preparation");
        }),
        ("strict story commands use the exported story identity across different native players", () =>
        {
            using var fixture = new Fixture();
            fixture.Player.Pointer = new IntPtr(101);
            fixture.Prime();
            Check(fixture.RecordStory("AdvanceScenario"), "preparation story command was refused");
            fixture.Frame(1600);
            fixture.FinishPreparationAndStartReplay();
            fixture.Player.Pointer = new IntPtr(202);
            fixture.Prime();
            Check(fixture.RecordStory("AdvanceScenario"),
                "equivalent selected story was rejected because the scene allocated a new native player");
            fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "matching story event could not finalize");
            finalize!.GetAwaiter().GetResult();
            fixture.Host.Update();
            Check(fixture.Session.Completed, "matching story event did not publish");
        }),
        ("strict story command divergence cancels before the native event executes", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            Check(fixture.RecordStory("AdvanceScenario"), "preparation story command was refused");
            fixture.Frame(1600);
            fixture.FinishPreparationAndStartReplay();
            fixture.Prime();
            fixture.Player.cur = 4;
            Check(!fixture.RecordStory("AdvanceScenario"), "changed story row was allowed to execute");
            Check(fixture.Session.Cancelled && !fixture.Session.Completed && !fixture.Host.Capturing,
                "event mismatch escaped the host failure path");
        }),
        ("strict story trace ignores events from an unrelated native player", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            var unrelated = new Test { Pointer = new IntPtr(999) };
            Check(fixture.RecordStory("AdvanceScenario", unrelated), "unrelated normal playback was blocked");
            fixture.Frame(1600);
            fixture.FinishPreparationAndStartReplay();
            fixture.Prime();
            fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "unrelated player contaminated the exported command trace");
            finalize!.GetAwaiter().GetResult();
        }),
        ("strict primer callback cancellation preserves the first command divergence", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            Check(fixture.RecordStory("AdvanceScenario"), "preparation story command was refused");
            fixture.Frame(1600);
            fixture.FinishPreparationAndStartReplay();
            fixture.Player.AdvanceAction = (_, _) =>
            {
                fixture.Player.cur = 4;
                fixture.RecordStory("AdvanceScenario");
            };
            fixture.Prime();
            var failures = UnityEngine.Debug.Messages.Where(m => m.Contains("AA Video Export: failure=")).ToArray();
            Check(failures.Length == 1 && failures[0].Contains("sandbox_replay_divergence"),
                "Update kept using disposed capture state and replaced the original command error: " + string.Join(" | ", failures));
            Check(!fixture.Host.Capturing && fixture.Session.Cancelled, "synchronous event failure left capture live");
        }),
        ("strict replay reaching fixed frame count without native End fails closed", () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareOneFrameAndStartReplay();
            fixture.Prime();
            fixture.Frame(1600);
            fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Session.Completed,
                "fixed length truncated an unfinished story instead of rejecting it");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("video_timeline_mismatch")), "missing native End not diagnosed");
        }),
        ("strict replay lets the terminal authored advance reach native End after the fixed frame", () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareOneFrameAndStartReplay();
            fixture.Prime();
            fixture.Frame(1600);
            Check(fixture.Get<bool>("_strictFinishPending"), "fixed replay frame did not arm the finish boundary");
            fixture.Player.AdvanceAction = (_, _) => fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Player.AdvanceScenario();
            Check(fixture.Player.AdvanceCalls == 2, "finish boundary blocked the terminal authored advance");
            Check(fixture.Get<ExportEndFence>("_endFence").FrameCount == 1,
                "terminal authored advance did not reach the native End fence");
            Check(!fixture.Host.AllowStrictScenarioAdvance(fixture.Player, null),
                "strict finish boundary allowed a second authored advance after native End");
        }),
        ("strict replay rejects changed story row before final output can commit", () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareOneFrameAndStartReplay();
            fixture.Prime();
            fixture.Player.cur = 2;
            fixture.Frame(1600);
            Check(fixture.Session.Cancelled && !fixture.Session.Completed,
                "same frame count hid changed story timing");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("timeline_replay_mismatch")), "story divergence not diagnosed");
        }),
        ("strict audio replay accepts split mixer blocks but preserves every nonzero PCM sample", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            float[] a = Enumerable.Repeat(.25f, 800 * 2).ToArray();
            float[] b = Enumerable.Repeat(.5f, 2400 * 2).ToArray();
            float[] expected = a.Concat(b).ToArray();
            fixture.FrameSamples(a);
            fixture.FrameSamples(b);
            fixture.FinishPreparationAndStartReplay();
            fixture.Prime();
            fixture.FrameSamples(expected.Take(3200).ToArray());
            fixture.FrameSamples(expected.Skip(3200).ToArray());
            Check(fixture.Session.WrittenAudio.SequenceEqual(expected), "fixed PCM replay lost, duplicated or reordered samples across mixer boundaries");
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            var finalize = fixture.Get<Task<ExportResult>?>("_finalize");
            Check(finalize != null, "split replay did not enter finalization: " + string.Join(" | ", UnityEngine.Debug.Messages.TakeLast(4)));
            finalize!.GetAwaiter().GetResult();
            fixture.Host.Update();
            Check(fixture.Session.Completed && fixture.Session.FramesWritten == 2 && fixture.Session.AudioSampleFrames == 3200,
                "equivalent PCM with different mixer partition failed its strict timeline");
        }),
        ("strict replay rejects differing PCM even when row and total sample counts match", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.FrameSamples(Enumerable.Repeat(.25f, 3200).ToArray());
            fixture.FinishPreparationAndStartReplay();
            fixture.Prime();
            fixture.Frame(1600);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Session.Completed, "different audio content was accepted based only on matching counts");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("audio_replay_mismatch")), "PCM content divergence was not diagnosed");
        }),
        ("strict drain ignores late capture callbacks and does not advance video", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Frame(1600);
            long captured = fixture.Captured;
            var native = fixture.Get<NativeCaptureScope>("_native");
            int audioCalls = native.AudioRenderCalls;
            fixture.Set("_strictDraining", true);
            fixture.Frame(1600);
            Check(fixture.Captured == captured, "capture callback advanced the frozen video frame count during drain");
            Check(fixture.Session.FramesWritten == 0 && native.AudioRenderCalls == audioCalls,
                "late capture callback wrote output or touched native audio while draining");
        }),
        ("repeated native End during drain cannot rearm or reset the drain bounds", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            fixture.Frame(1600);
            // Keep the fixed end two frames away and return an empty native
            // block so the bounded drain remains observable for this check.
            fixture.Set("_captured", 2L);
            fixture.Set("_strictFinishPending", true);
            fixture.Get<ExportEndFence>("_endFence").ObserveEnd(2, UnityEngine.Time.frameCount, UnityEngine.Time.frameCount);
            NativeCaptureScope.AudioSampleCounts.Enqueue(0);
            fixture.Host.Update();
            Check(fixture.Get<bool>("_strictDraining"), "drain completed before the native boundary was available");
            int updates = fixture.Get<int>("_strictDrainUpdates");
            double started = fixture.Get<double>("_strictDrainStarted");
            Check(!fixture.Host.BeforePlayerEnd(fixture.Player) && !fixture.Host.BeforePlayerEnd(fixture.Player),
                "native End escaped the strict drain fence");
            Check(fixture.Get<bool>("_strictDraining") && fixture.Get<int>("_strictDrainUpdates") == updates &&
                fixture.Get<double>("_strictDrainStarted") == started,
                "repeated End rearmed or reset strict drain bounds");
        }),
        ("strict drain waits for a real 128-sample tail instead of padding the frozen story", () =>
        {
            using var fixture = new Fixture();
            fixture.Prime();
            for (int index = 0; index < 11; index++) fixture.Frame(1600);
            fixture.Frame(1472);
            Check(fixture.Captured == 12, "short-tail fixture did not freeze the expected video frame count");
            NativeCaptureScope.AudioSampleCounts.Enqueue(128);
            fixture.Host.BeforePlayerEnd(fixture.Player);
            fixture.Host.Update();
            Check(fixture.Captured == 12 && fixture.Get<bool>("_strictReplayPending") &&
                fixture.Session.AudioSampleFrames == 0 && !fixture.Session.Completed,
                "128 native samples were padded, trimmed, or emitted as a video frame");
            Check(!UnityEngine.Debug.Messages.Any(m => m.Contains("audio_timeline_incomplete")),
                "available 128-sample native tail was rejected as a shortage");
        }),
        ("strict replay rejects a missing native End before requesting a drain block", () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareOneFrameAndStartReplay();
            fixture.Prime();
            fixture.Frame(1599);
            var native = fixture.Get<NativeCaptureScope>("_native");
            int callsBeforeUpdate = native.AudioRenderCalls;
            fixture.Host.Update();
            Check(fixture.Session.Cancelled && !fixture.Session.Completed,
                "replay without native End did not fail closed");
            Check(native.AudioRenderCalls == callsBeforeUpdate,
                "missing native End entered audio drain before rejecting the timeline");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("video_timeline_mismatch")),
                "missing native End was not diagnosed");
        }),
        ("strict replay rejects source and native timing-setting changes between passes", () =>
        {
            foreach (bool source in new[] { true, false })
            {
                using var fixture = new Fixture();
                fixture.Prime();
                fixture.Frame(1600);
                if (source) File.AppendAllText(fixture.Story, "modified");
                fixture.Host.BeforePlayerEnd(fixture.Player);
                fixture.Host.Update();
                if (!source) UserSettings.Instance!.data.autoDelay++;
                fixture.WaitReplaySourceCheck();
                fixture.Host.Update();
                Check(fixture.Session.Cancelled && !fixture.Session.Completed && !fixture.Get<bool>("_armed"),
                    "changed source or timing settings started video replay");
                Check(UnityEngine.Debug.Messages.Any(m => m.Contains("timeline_source_changed")), "changed replay identity was not diagnosed");
            }
        }),
        ("strict cancellation in preparation and replay cleans PCM and restores ownership", () =>
        {
            foreach (bool replay in new[] { false, true })
            {
                using var fixture = new Fixture();
                if (replay) fixture.PrepareOneFrameAndStartReplay();
                fixture.Prime();
                fixture.Call("Cancel");
                fixture.Cleanup();
                Check(!fixture.Host.Capturing && fixture.Session.Cancelled && !fixture.Session.Completed,
                    "cancelled pass retained capture or published output");
                Check(!RenderControlV1.IsRenderingOwned, "cancelled pass leaked render ownership");
                Check(!Directory.EnumerateFiles(fixture.Output, ".fixture-prepared-*").Any() &&
                    !Directory.EnumerateFiles(fixture.Output, ".aave-transcript-*").Any(), "cancelled pass retained temporary PCM or transcript");
            }
        })
    };

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void WithSharedBaseline(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "aave-repeat-host-" + Guid.NewGuid().ToString("N"));
        try { test(directory); }
        finally { ControlledBackgroundVideoAdapter.Commands = Array.Empty<ControlledBackgroundCommand>(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static string CompleteBaselineMovie(string directory, int effectFrame = 0, long? backgroundFrame = null,
        string? voiceHash = null, string storyContents = "same frozen story", bool reject = false)
    {
        using var fixture = new Fixture(receiptDirectory: directory, storyContents: storyContents);
        if (voiceHash != null) CharacterVoiceSessionAdapter.ReceiptTraceSha256 = voiceHash;
        ControlledBackgroundVideoAdapter.Commands = backgroundFrame.HasValue
            ? new[] { new ControlledBackgroundCommand(0, backgroundFrame.Value, "Start", 0, "background.png", new string('a', 64), 0) }
            : Array.Empty<ControlledBackgroundCommand>();
        string identity = fixture.Get<string>("_strictIdentity");
        void Frames()
        {
            fixture.Prime();
            for (int frame = 0; frame < 2; frame++)
            {
                if (frame == effectFrame) fixture.Host.RecordStrictSandboxCommand("fixture-effect", "Begin", "authored-effect-1", new string('b', 64));
                fixture.Frame(1600);
            }
        }
        Frames();
        if (reject)
        {
            fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update(); fixture.Cleanup();
            Check(fixture.Session.Cancelled && !fixture.Session.Completed
                && UnityEngine.Debug.Messages.Any(message => message.Contains("timeline_repeat_mismatch", StringComparison.Ordinal)),
                "changed output events were not rejected under the existing input key");
            return identity;
        }
        fixture.FinishPreparationAndStartReplay(); Frames();
        fixture.Host.BeforePlayerEnd(fixture.Player); fixture.Host.Update();
        fixture.Get<Task<ExportResult>>("_finalize").GetAwaiter().GetResult(); fixture.Host.Update();
        Check(fixture.Session.Completed, "fixture's initial strict movie did not complete");
        return identity;
    }
    private static void RejectBaselineMovie(string directory, int effectFrame = 0, long? backgroundFrame = null, string? voiceHash = null)
        => CompleteBaselineMovie(directory, effectFrame, backgroundFrame, voiceHash, reject: true);

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "aave-strict-host-" + Guid.NewGuid().ToString("N"));
        private readonly int _previousSampleRate;
        internal string Output { get; }
        internal string Story { get; }
        internal ExportHost Host { get; }
        internal ExportSession Session { get; }
        internal Test Player { get; private set; } = null!;
        internal long Captured => Get<long>("_captured");

        internal Fixture(int sampleRate = 48000, int fps = 30, bool staticInertPresent = false, bool begin = true,
            string? receiptDirectory = null, string storyContents = "same frozen story")
        {
            UnityEngine.Object.ResetRegistry();
            UnityEngine.Debug.Messages.Clear();
            UnityEngine.Time.frameCount = 0;
            _previousSampleRate = UnityEngine.AudioSettings.outputSampleRate;
            UnityEngine.AudioSettings.outputSampleRate = sampleRate;
            NativeCaptureScope.AudioSampleCounts.Clear();
            NativeCaptureScope.AudioBlocks.Clear();
            Output = Path.Combine(_root, "output");
            Directory.CreateDirectory(Output);
            string saves = Path.Combine(_root, "data", "saves");
            Directory.CreateDirectory(saves);
            Story = Path.Combine(saves, "fixture.aas");
            File.WriteAllText(Story, storyContents);
            UserSettings.Instance = new() { WorkspacePath = _root };
            ScenarioResourceManager.Instance = new();
            Plugin.TimelineReceiptDirectory = receiptDirectory ?? Path.Combine(_root, "receipts");
            Host = new ExportHost(new IntPtr(123)); Host.Awake();
            StaticInertModEvidence.Present=staticInertPresent;
            if (ExportBuildProfile.DevelopmentAuditEnabled)
                typeof(ExportHost).GetField("_staticInertScope",BindingFlags.Instance|BindingFlags.NonPublic)?.SetValue(Host,StaticInertScope.Acquire());
            Set("_probeAttempted", true); Set("_capabilities", new FfmpegCapabilities());
            Set("_probeMode", "hardware"); Set("_capabilitiesMode", "hardware");
            var options = new ExportOptions { Title = "strict", OutputDirectory = Output, Fps = fps, AudioQuality = "pcm24", StrictSynchronization = true };
            Session = new ExportSession(options, "", "", "fixture", sampleRate, 2, true);
            Set("_options", options); Set("_session", Session); Set("_cancellation", new CancellationTokenSource());
            Set("_selectedStoryKey", Story);
            Set("_selectedStory", new CatalogFileInfo { block = new() { path = Story }, controlPanel = new() });
            Get<NativeExportPanel>("_panel").Options = options;
            Set("_frozenSave", new GenericScenarioExcelTable());
            if (!begin) { Player = NewPlayer(); return; }
            StartStrict();
        }

        internal void StartStrict()
        {
            var menuField = typeof(ExportHost).GetField("_retainedMenuLease", BindingFlags.Instance | BindingFlags.NonPublic);
            if (menuField != null) { RetainedMenuPlaybackBoundary.TryAcquireRetained(out var lease); menuField.SetValue(Host, lease); }
            var voiceField = typeof(ExportHost).GetField("_voiceSession", BindingFlags.Instance | BindingFlags.NonPublic);
            if (voiceField != null && CharacterVoiceSessionAdapter.IsProviderLoaded)
                voiceField.SetValue(Host, CharacterVoiceSessionAdapter.Attach(_ => { }, _ => { }));
            var recipeField = typeof(ExportHost).GetField("_recipeScope", BindingFlags.Instance | BindingFlags.NonPublic);
            var frameworkField = typeof(ExportHost).GetField("_frameworkScope", BindingFlags.Instance | BindingFlags.NonPublic);
            if (ExportBuildProfile.DevelopmentAuditEnabled && frameworkField != null) frameworkField.SetValue(Host, FrameworkGeneratedAssemblyScope.Acquire());
            if (ExportBuildProfile.DevelopmentAuditEnabled && recipeField != null) recipeField.SetValue(Host, RetainedRecipeScope.Acquire(Plugin.LogPerformance));
            Call("InitializeStrictPreparation", Story);
            Task.Run(() => Call("PrepareStrictIdentity", Story)).GetAwaiter().GetResult();
            typeof(ExportHost).GetMethod("BindFrozenVoiceStory", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(Host, null);
            Player = NewPlayer();
            Call("Begin", Player);
            Player.AdvanceScenario();
        }

        private Test NewPlayer()
        {
            SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
            return new Test
            {
                EndAction = () => SceneManager.Active = new() { name = "CatalogScene", isLoaded = true }
            };
        }

        internal void Prime()
        {
            Frame(0);
            Frame(1600);
            Host.Update();
            Check(Player.AdvanceCalls == 1 && Captured == 0, "primer did not release exactly one native first event");
        }

        internal void Frame(int sampleFrames) { UnityEngine.Time.frameCount++; CaptureCurrent(sampleFrames); }
        internal void CaptureCurrent(int sampleFrames)
        {
            NativeCaptureScope.AudioSampleCounts.Enqueue(sampleFrames);
            Call("CaptureFrame", true);
        }
        internal void FrameSamples(float[] samples)
        {
            UnityEngine.Time.frameCount++;
            NativeCaptureScope.AudioBlocks.Enqueue(samples);
            Call("CaptureFrame", true);
        }

        internal void PrepareOneFrameAndStartReplay()
        {
            Prime();
            Frame(1600);
            FinishPreparationAndStartReplay();
        }

        internal void FinishPreparationAndStartReplay(bool expectCapture = true)
        {
            Host.BeforePlayerEnd(Player);
            Host.Update();
            Check(Get<bool>("_strictReplayPending"), "audio preparation did not seal: " + string.Join(" | ", UnityEngine.Debug.Messages.TakeLast(6)));
            WaitReplaySourceCheck();
            Host.Update();
            Check(Get<bool>("_armed"), "catalog reload did not arm replay");
            Player = NewPlayer();
            Host.MarkNewPlayback(Player);
            Player.AdvanceScenario();
            if (expectCapture) Check(Host.Capturing && !Get<bool>("_strictPreparing"), "new playback did not begin video replay");
        }

        internal T Get<T>(string name) => (T)typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
        internal StaticInertScope InertScope
        {
            get { var scopeField=typeof(ExportHost).GetField("_staticInertScope",BindingFlags.Instance|BindingFlags.NonPublic);Check(scopeField!=null,"Host has no retained static inert scope");return (StaticInertScope)scopeField!.GetValue(Host)!; }
        }
        internal void Set(string name, object? value) => typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Host, value);
        internal void Call(string name, params object[] args)
        {
            var method = typeof(ExportHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Check(method != null, "missing production host lifecycle callback: " + name);
            method!.Invoke(Host, args);
        }
        internal string WaitReplaySourceCheck() => Get<Task<string>>("_strictReplaySourceCheck").WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        internal void SetReplaySourceCheck(Task<string> check) => Set("_strictReplaySourceCheck", check);

        internal void AttachStartupBoundary(bool present = true)
        {
            var field = typeof(ExportHost).GetField("_strictStartupBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(field != null, "host has no retained startup-controller scope");
            AASkipIntroExportBoundary.Present = present;
            field!.SetValue(Host, AASkipIntroExportBoundary.TryAcquire());
            typeof(ExportHost).GetField("_strictStartupBoundaryAcquired", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(Host, true);
        }

        internal bool RecordStory(string operation, Test? player = null)
        {
            var method = typeof(ExportHost).GetMethod("RecordStrictStoryCommand", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(method != null, "selected-story command boundary is missing");
            return (bool)method!.Invoke(Host, new object[] { player ?? Player, operation, false })!;
        }

        internal void Cleanup()
        {
            Get<Task?>("_cleanup")?.GetAwaiter().GetResult();
            Host.Update();
        }

        public void Dispose()
        {
            Get<Task<string>?>("_strictReplaySourceCheck")?.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Host.Shutdown();
            Get<Task?>("_cleanup")?.GetAwaiter().GetResult();
            UserSettings.Instance = null;
            UnityEngine.AudioSettings.outputSampleRate = _previousSampleRate;
            NativeCaptureScope.AudioSampleCounts.Clear();
            NativeCaptureScope.AudioBlocks.Clear();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
