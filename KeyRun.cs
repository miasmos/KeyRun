using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Diagnostics;
using System.Configuration;
using System.Windows.Markup;
using System.Windows.Media;
using Zeta.Common;
using Zeta.Common.Plugins;
using Zeta.Common.Xml;
using Zeta.Bot;
using Zeta.Bot.Profile;
using Zeta.Bot.Profile.Composites;
using Zeta.Game;
using Zeta.Game.Internals.Actors;
using Zeta.Bot.Navigation;
using Zeta.TreeSharp;
using Zeta.XmlEngine;

namespace KeyRun
{
    [XmlElement("KeyRunSettings")]
    class KeyRunSettings : XmlSettings
    {
        private static KeyRunSettings _instance;
        private bool avoidCombat;
        private float keywardenHpThreshold;
        private static string _battleTagName;

        public static string BattleTagName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_battleTagName) && ZetaDia.Service.Hero.IsValid)
                    _battleTagName = ZetaDia.Service.Hero.BattleTagName;
                return _battleTagName;
            }
        }

        public KeyRunSettings() :
            base(Path.Combine(SettingsDirectory, "KeyRun", BattleTagName, "KeyRunSettings.xml"))
        {
        }

        public static KeyRunSettings Instance
        {
            get { return _instance ?? (_instance = new KeyRunSettings()); }
        }

        [XmlElement("AvoidCombat")]
        [DefaultValue(false)]
        [Setting]
        public bool AvoidCombat
        {
            get
            {
                return avoidCombat;
            }
            set
            {
                avoidCombat = value;
                OnPropertyChanged("AvoidCombat");
            }
        }

        [XmlElement("KeywardenHpThreshold")]
        [DefaultValue(350000f)]
        [Setting]
        public float KeywardenHpThreshold
        {
            get
            {
                return keywardenHpThreshold;
            }
            set
            {
                keywardenHpThreshold = value;
                OnPropertyChanged("KeywardenHpThreshold");
            }
        }
    }

    class KeyRunConfig
    {
        public int ServerPort { get; set; }

        private static Window configWindow;

        public static void CloseWindow()
        {
            configWindow.Close();
        }

        public static Window GetDisplayWindow()
        {
            if (configWindow == null)
            {
                configWindow = new Window();
            }

            string assemblyPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            string xamlPath = Path.Combine(assemblyPath, "Plugins", "KeyRun", "KeyRun.xaml");
            string xamlContent = File.ReadAllText(xamlPath);

            UserControl mainControl = (UserControl)XamlReader.Load(new MemoryStream(Encoding.UTF8.GetBytes(xamlContent)));
            configWindow.DataContext = KeyRunSettings.Instance;
            configWindow.Content = mainControl;
            configWindow.Width = 450;
            configWindow.Height = 100;
            configWindow.ResizeMode = ResizeMode.NoResize;
            configWindow.Title = "KeyRun Settings";
            configWindow.Closed += ConfigWindow_Closed;
            Demonbuddy.App.Current.Exit += ConfigWindow_Closed;

            return configWindow;
        }

        static void ConfigWindow_Closed(object sender, System.EventArgs e)
        {
            KeyRunSettings.Instance.Save();
            if (configWindow != null)
            {
                configWindow.Closed -= ConfigWindow_Closed;
                configWindow = null;
            }
        }
    }

    public static class Logger
    {
        private static readonly log4net.ILog Logging = Zeta.Common.Logger.GetLoggerInstanceForType();

        public static void Log(string message, params object[] args)
        {
            StackFrame frame = new StackFrame(1);
            var method = frame.GetMethod();
            var type = method.DeclaringType;

            Logging.InfoFormat("[KeyRun] " + string.Format(message, args), type.Name);
        }

        public static void Log(string message)
        {
            Log(message, string.Empty);
        }

    }



    public class KeyRun : IPlugin
    {
        static readonly string NAME = "KeyRun_Toky";
        static readonly string AUTHOR = "skeetermcdiggles & Magi & deeghs_Edit by Toky";
        static readonly Version VERSION = new Version(1, 9, 2);
        static readonly string DESCRIPTION = "KeyRun Hunting and Act Choosing Based on Keys Collected";

        public static bool ChooseActProfileExitGame = false;
        public static string ChooseActProfile = "";
        public static string KeyWardenProfileCurrentlyRunning = "";

        private int[] _keywardenSNOs = { 255704, 256022, 256040, 256054 };
        private int[] _keySNOs = { 364694, 364695, 364696, 364697 };
        public bool _keydropped = false;
        public bool _keywardenFound = false;
        public bool _keywardenDead = false;
        public bool _keywardenFindCorpse = false;
        public bool _keywardenReadyForWarp = false;
        private float _keywardenCurrentHitpoints = 0f;

        // current location of keywarden
        public static Vector3 _blankVector = new Vector3(0, 0, 0);
        public static Vector3 _keywardenPosition = _blankVector;

        // distance from Keywarden
        public static float _distanceFromKeywarden = 0f;
        internal static bool IsKeyRunProfile = false;

        // Plugin Auth Info
        public string Author
        {
            get
            {
                return AUTHOR;
            }
        }
        public string Description
        {
            get
            {
                return DESCRIPTION;
            }
        }
        public string Name
        {
            get
            {
                return NAME;
            }
        }
        public Version Version
        {
            get
            {
                return VERSION;
            }
        }
        public Window DisplayWindow { get { return KeyRunConfig.GetDisplayWindow(); } }


        ///////////////
        // DB EVENTS //
        public void OnDisabled()
        {
            GameEvents.OnPlayerDied -= KeyRunOnDeath;
            GameEvents.OnGameChanged -= KeyRunOnGameChange;
            GameEvents.OnWorldTransferStart -= KeyRunOnWorldTransferStart;
            ProfileManager.OnProfileLoaded -= KeyRunOnProfileChange;
        }

        public void OnEnabled()
        {
            GameEvents.OnPlayerDied += KeyRunOnDeath;
            GameEvents.OnGameChanged += KeyRunOnGameChange;
            GameEvents.OnWorldTransferStart += KeyRunOnWorldTransferStart;
            ProfileManager.OnProfileLoaded += KeyRunOnProfileChange;

            Log("************************************");
            Log("ENABLED: KeyRun " + Version + " now in action!");
            Log("************************************");
        }

        public void OnInitialize()
        {
        }

        public void OnPulse()
        {
            if (ZetaDia.Me.HitpointsCurrentPct > 0 && !_keywardenDead)
                KeyWardenCheck();

            if (ZetaDia.Me.HitpointsCurrentPct > 0 && _keywardenDead)
                InfernalKeyCheck();
        }

        public void OnShutdown()
        {
        }


        ////////////////////
        // CORE FUNCTIONS //
        // KeyRunOnGameChange
        private void KeyRunOnGameChange(object src, EventArgs mea)
        {
            Log("New Game Started, Reset Variables");
            _keydropped = false;
            _keywardenFound = false;
            _keywardenDead = false;
            _keywardenFindCorpse = false;
            _keywardenReadyForWarp = false;
            _keywardenCurrentHitpoints = 0f;
            _distanceFromKeywarden = 0f;
            _keywardenPosition = _blankVector;
            KeyWardenProfileCurrentlyRunning = "";
            ChooseActProfileExitGame = false;
            ChooseActProfile = "";
        }

        // KeyRunOnWorldChange
        private void KeyRunOnWorldTransferStart(object src, EventArgs mea)
        {
            KeyRunAlterCombatBehavior(false);
        }

        // KeyRunOnDeath
        private void KeyRunOnDeath(object src, EventArgs mea)
        {
            KeyRunAlterCombatBehavior(false);

            if (_keywardenFound)
            {
                // reset warden if found so we don't skip him
                //Log("========== DIED BEFORE KILLING KEYWARDEN - RETURN TO WARDEN LOCATION ============");
                //_keywardenFound = false;
            }
            else if (KeyWardenProfileCurrentlyRunning != "" && !_keywardenReadyForWarp)
            {
                // keywarden is dead, but so are you. Probably should go back
                Log("========== KILLED KEYWARDEN BUT DIED - LET'S GO BACK TO BE SAFE ============");
                // set find corpse boolean
                _keywardenFindCorpse = true;
            }

        }

        // KeyRunOnProfileChange
        private void KeyRunOnProfileChange(object src, EventArgs mea)
        {
            Log("Profile changed, let's assume it's not a KeyRun profile ...");
            IsKeyRunProfile = false;
        }

        public void KeyRunAlterCombatBehavior(bool enableCombat)
        {
            if (IsKeyRunProfile && KeyRunSettings.Instance.AvoidCombat)
            {
                Log(string.Format("A KeyRun enabled profile changed combat setting to {0}", enableCombat));
                ProfileManager.CurrentProfile.KillMonsters = enableCombat;
            }
        }

        // InfernalKeyCheck
        private void InfernalKeyCheck()
        {
            // set key object
            DiaObject keyObject = ActorList.OfType<DiaObject>().FirstOrDefault(r => _keySNOs.Any(k => k == r.ActorSNO));
            //DiaObject keyObject = ZetaDia.Actors.RActorList.OfType<DiaObject>().FirstOrDefault(r => _keyNames.Any(k => r.Name.StartsWith(k)));

            // key check
            if (keyObject != null)
            {
                if (!_keydropped)
                {
                    Log("========== INFERNAL KEY DROPPED ==========");

                    // key dropped
                    _keydropped = true;
                }

                // Get Key if we aren't try to vendor or do something
                if (!Zeta.Bot.Logic.BrainBehavior.IsVendoring)
                {

                    // Go Get Key if Trinity doesn't *spammed to ensure nothing prevents us from getting that key!*
                    Log("========== RETRIEVING INFERNAL KEY ==========");
                    ZetaDia.Me.UsePower(SNOPower.Axe_Operate_Gizmo, Vector3.Zero, 0, keyObject.ACDGuid);
                }

            }
            else if (keyObject == null && _keydropped && !ZetaDia.IsInTown && !Zeta.Bot.Logic.BrainBehavior.IsVendoring)
            {
                Log("=+=+=+=+=+= INFERNAL KEY ACQUIRED!!! =+=+=+=+=+=");

                // key dropped
                _keydropped = false;
                KeyWardenWarpOut();
            }
            else if (keyObject == null && !_keydropped && !ZetaDia.IsInTown && !Zeta.Bot.Logic.BrainBehavior.IsVendoring)
            {
                Log("=========== NO KEY DROPPED ===========");
                KeyWardenWarpOut();
            }
        }

        // KeyWardenCheck
        private void KeyWardenCheck()
        {
            // set keywarden object
            DiaObject wardenObject = ActorList.OfType<DiaObject>().FirstOrDefault(r => _keywardenSNOs.Any(k => k == r.ActorSNO));

            // set distance from keywarden
            if (_distanceFromKeywarden >= 0f && !_keywardenDead) _distanceFromKeywarden = ZetaDia.Me.Position.Distance(_keywardenPosition);

            // perform check
            if (wardenObject != null)
            {
                KeyWardenUpdateStats(wardenObject);		// update keywarden stats

                if (_keywardenCurrentHitpoints > 0f && !_keywardenFound)
                {
                    KeyWardenFound();					// found
                }
                else if (_keywardenCurrentHitpoints <= 0f && !_keywardenDead)
                {
                    KeyWardenDead();					// defintely dead
                }
            }
            else if (_keywardenFound && !_keywardenDead)
            {
                KeyWardenOutOfRange();					// out of range or possibly dead
            }
            else if (_keywardenDead && !_keywardenReadyForWarp && !_keywardenFindCorpse)
            {
                KeyWardenGoToCorpse();					// confirmed dead, go to corpse
            }
            else if (_keywardenFindCorpse && !_keywardenReadyForWarp)
            {
                KeyWardenLookForCorpse();				// Keywarden dead, but so are you, find his corpse
            }
            else if (_keywardenReadyForWarp && !_keydropped)
            {
                KeyWardenWarpOut();						// Keywarden dead, found corpse, looted, lets restart!
            }
        }

        private static System.Collections.Generic.IEnumerable<Actor> _actorList;
        public static System.Collections.Generic.IEnumerable<Actor> ActorList
        {
            get
            {
                _actorList = ZetaDia.Actors.RActorList;
                return _actorList;
            }
        }

        // KeyWardenUpdateStats
        private void KeyWardenUpdateStats(DiaObject wardenObject)
        {
            // set keywarden hitpoints
            _keywardenCurrentHitpoints = wardenObject.CommonData.GetAttribute<float>(ActorAttributeType.HitpointsCur);
            // set keywarden location
            _keywardenPosition = wardenObject.Position;
        }

        // KeyWardenFound
        private void KeyWardenFound()
        {
            _keywardenFound = true;
            _keywardenDead = false;
            Log("========== KEYWARDEN FOUND ============");
            KeyWardenGoToPosition();
            KeyRunAlterCombatBehavior(true);
        }

        // KeyWardenOutOfRange
        private void KeyWardenOutOfRange()
        {
            Log("WARDEN HP: " + _keywardenCurrentHitpoints);

            if (_keywardenCurrentHitpoints > KeyRunSettings.Instance.KeywardenHpThreshold)
            {
                // assume keywarden is out of range
                Log("========== KEYWARDEN OUT OF RANGE ============");
                KeyRunAlterCombatBehavior(false);
                Log("Let's move to the location we last saw him ...");

                _keywardenFound = false;
                KeyWardenGoToPosition();
            }
            else
            {
                // assume keywarden is dead
                KeyWardenDead();
            }
        }

        // KeyWardenDead
        private void KeyWardenDead()
        {
            _keywardenFound = false;
            _keywardenDead = true;

            // Set Current Profile in case we die after killing Keywarden
            KeyWardenProfileCurrentlyRunning = Zeta.Bot.Settings.GlobalSettings.Instance.LastProfile;

            Log("=+=+=+=+=+= KEYWARDEN VANQUISHED!!! =+=+=+=+=+=");
        }

        // KeyWardenLookForCorpse
        private void KeyWardenLookForCorpse()
        {
            // set distance from keywardem
            float distanceFromKeywarden = ZetaDia.Me.Position.Distance(_keywardenPosition);
            //Log("Distance from Warden = " + distanceFromKeywarden);

            // corpse within range
            if (distanceFromKeywarden < 60f && ZetaDia.Me.HitpointsCurrentPct > 0)
            {
                // go to corpse (currently cycles as there are likely enemies lurking)
                KeyWardenGoToCorpse();
            }
        }

        // KeyWardenGoToPosition
        public static void KeyWardenGoToPosition()
        {
            // Move to Warden Location
            Navigator.MoveTo(_keywardenPosition, "Last known Keywarden position");
        }

        // KeyWardenGoToCorpse
        private void KeyWardenGoToCorpse()
        {
            // Don't do anything until done doing shit
            if (Zeta.Bot.Logic.BrainBehavior.IsVendoring)
            {
                return;
            }

            // Get distance to keywarden corpse
            float distanceFromKeywarden = ZetaDia.Me.Position.Distance(_keywardenPosition);

            // Move to Keywarden's last known position to pick up items
            if (distanceFromKeywarden > 6f)
            {
                // Don't do anything
                if (Zeta.Bot.Logic.BrainBehavior.IsVendoring)
                {
                    return;
                }

                // Moving to Keywarden Corpse
                Log("Moving to last known Keywarden Location to ensure item pickup...");
                KeyWardenGoToPosition();

                // Sleep
                Thread.Sleep(1000);
            }
            else
            {
                // Found corpse, reset trinity loot parameters for final pickup
                Log("Keywarden Corpse Found...");

                // Don't do anything
                if (Zeta.Bot.Logic.BrainBehavior.IsVendoring)
                {
                    return;
                }

                // Ready for Warp
                _keywardenReadyForWarp = true;
            }
        }

        // KeyWardenWarpOut
        private void KeyWardenWarpOut()
        {
            // Reset variables for warp
            _keywardenReadyForWarp = false;
            _keywardenDead = false;
            _keywardenFindCorpse = false;

            // Get ready for new Act
            Log("Ready to choose new Act.");

            // Load next profile
            ProfileManager.Load(ChooseActProfile);
            // A quick nap-time helps prevent some funny issues
            Thread.Sleep(3000);
            // See if we need to exit the game
            if (ChooseActProfileExitGame)
            {
                Log("Exiting game to continue with next profile.");
                // Attempt to teleport to town first for a quicker exit
                int iSafetyLoops = 0;
                while (!ZetaDia.IsInTown)
                {
                    iSafetyLoops++;
                    ZetaDia.Me.UsePower(SNOPower.UseStoneOfRecall, ZetaDia.Me.Position, ZetaDia.Me.WorldDynamicId, -1);
                    Thread.Sleep(1000);
                    if (iSafetyLoops > 5)
                        break;
                }
                Thread.Sleep(1000);
                ZetaDia.Service.Party.LeaveGame();
                // Wait for 10 second log out timer if not in town, else wait for 3 seconds instead
                Thread.Sleep(!ZetaDia.IsInTown ? 10000 : 3000);
            }
        }

        // Log / Ancillary
        private void Log(string message, LogLevel logLevel = LogLevel.Info)
        {
            Logger.Log(message);
        }

        public static bool isPlayerDoingAnything()
        {
            if (Zeta.Bot.Logic.BrainBehavior.IsVendoring)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        public bool Equals(IPlugin other)
        {
            return Name.Equals(other.Name) && Author.Equals(other.Author) && Version.Equals(other.Version);
        }
    }

    //////////////
    // XML TAGS //
    // Enable certain KeyRun features for profile
    [XmlElement("KeyRunEnabledProfile")]
    public class KeyRunEnabledProfile : ProfileBehavior
    {
        private bool m_IsDone = false;
        public override bool IsDone
        {
            get
            {
                return m_IsDone;
            }
        }

        protected override Composite CreateBehavior()
        {
            return new Zeta.TreeSharp.Action(ret =>
            {
                Log("Profile is a KeyRun profile!");
                KeyRun.IsKeyRunProfile = true;
                m_IsDone = true;
            });
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }

        private void Log(string message, LogLevel logLevel = LogLevel.Info)
        {
            Logger.Log(message);
        }
    }

    // Force Town Warp
    [XmlElement("KeyRunForceTownWarp")]
    public class KeyRunForceTownWarp : ProfileBehavior
    {
        private bool m_IsDone = false;
        public override bool IsDone
        {
            get
            {
                return m_IsDone;
            }
        }

        protected override Composite CreateBehavior()
        {
            return new Zeta.TreeSharp.Action(ret =>
            {
                // Attempt to teleport to town first for a quicker exit
                int iSafetyLoops = 0;
                while (iSafetyLoops <= 5)
                {
                    iSafetyLoops++;
                    ZetaDia.Me.UsePower(SNOPower.UseStoneOfRecall, ZetaDia.Me.Position, ZetaDia.Me.WorldDynamicId, -1);
                    //Thread.Sleep(1000);
                }

                m_IsDone = true;
            });
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }
    }

    // IfReadyToWarp
    [XmlElement("IfReadyToWarp")]
    public class IfReadyToWarp : ComplexNodeTag
    {
        private bool? bComplexDoneCheck;
        private bool? bAlreadyCompleted;
        private Func<bool> funcConditionalProcess;
        private static Func<ProfileBehavior, bool> funcBehaviorProcess;

        protected override Composite CreateBehavior()
        {
            PrioritySelector decorated = new PrioritySelector(new Composite[0]);
            foreach (ProfileBehavior behavior in base.GetNodes())
            {
                decorated.AddChild(behavior.Behavior);
            }
            return new Zeta.TreeSharp.Decorator(new CanRunDecoratorDelegate(CheckNotAlreadyDone), decorated);
        }

        public bool GetConditionExec()
        {
            // If trinity is doing shit, we're not ready
            bool flag = KeyRun.isPlayerDoingAnything();

            // check for reverse
            if (Type != null && Type == "reverse")
            {
                return !flag;
            }
            else
            {
                return flag;
            }
        }

        private bool CheckNotAlreadyDone(object object_0)
        {
            return !IsDone;
        }

        public override void ResetCachedDone()
        {
            foreach (ProfileBehavior behavior in Body)
            {
                behavior.ResetCachedDone();
            }
            bComplexDoneCheck = null;
        }

        private static bool CheckBehaviorIsDone(ProfileBehavior profileBehavior)
        {
            return profileBehavior.IsDone;
        }

        [XmlAttribute("type")]
        public string Type { get; set; }

        public Func<bool> Conditional
        {
            get
            {
                return funcConditionalProcess;
            }
            set
            {
                funcConditionalProcess = value;
            }
        }


        public override bool IsDone
        {
            get
            {
                // Make sure we've not already completed this tag
                if (bAlreadyCompleted.HasValue && bAlreadyCompleted == true)
                {
                    return true;
                }
                if (!bComplexDoneCheck.HasValue)
                {
                    bComplexDoneCheck = new bool?(GetConditionExec());
                }
                if (bComplexDoneCheck == false)
                {
                    return true;
                }
                if (funcBehaviorProcess == null)
                {
                    funcBehaviorProcess = new Func<ProfileBehavior, bool>(CheckBehaviorIsDone);
                }
                bool bAllChildrenDone = Body.All<ProfileBehavior>(funcBehaviorProcess);
                if (bAllChildrenDone)
                {
                    bAlreadyCompleted = true;
                }
                return bAllChildrenDone;
            }
        }
    }

    // GoToKeyWarden
    [XmlElement("GoToKeyWarden")]
    public class GoToKeyWarden : ProfileBehavior
    {
        private bool m_IsDone = false;
        private float fPathPrecision;
        private Vector3 kVector = KeyRun._keywardenPosition;
        private Vector3 blankVector = KeyRun._blankVector;

        protected override Composite CreateBehavior()
        {
            Composite[] children = new Composite[2];
            Composite[] compositeArray = new Composite[2];
            compositeArray[0] = new Zeta.TreeSharp.Action(new ActionSucceedDelegate(FlagTagAsCompleted));
            children[0] = new Zeta.TreeSharp.Decorator(new CanRunDecoratorDelegate(CheckDistance), new Sequence(compositeArray));
            ActionDelegate actionDelegateMove = new ActionDelegate(MoveToKeywarden);
            Sequence sequenceblank = new Sequence(
                new Zeta.TreeSharp.Action(actionDelegateMove)
                );
            children[1] = sequenceblank;
            return new PrioritySelector(children);
        }

        private RunStatus MoveToKeywarden(object ret)
        {
            if (kVector != blankVector)
            {
                KeyRun.KeyWardenGoToPosition();
            }

            return RunStatus.Success;
        }

        private bool CheckDistance(object object_0)
        {
            if (kVector != blankVector)
            {
                return (ZetaDia.Me.Position.Distance(kVector) <= Math.Max(PathPrecision, Navigator.PathPrecision));
            }
            return true;
        }

        private void FlagTagAsCompleted(object object_0)
        {
            m_IsDone = true;
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }

        public override bool IsDone
        {
            get
            {
                if (IsActiveQuestStep)
                {
                    return m_IsDone;
                }
                return true;
            }
        }

        [XmlAttribute("pathPrecision")]
        public float PathPrecision
        {
            get
            {
                return fPathPrecision;
            }
            set
            {
                fPathPrecision = value;
            }
        }
    }

    // GetAwayFromKeyWarden
    [XmlElement("GetAwayFromKeyWarden")]
    public class GetAwayFromKeyWarden : ProfileBehavior
    {
        private bool m_IsDone = false;
        private float fPosX;
        private float fPosY;
        private float fPosZ;
        private float fPathPrecision;
        private Vector3? vMainVector;

        protected override Composite CreateBehavior()
        {
            Composite[] children = new Composite[2];
            Composite[] compositeArray = new Composite[2];
            compositeArray[0] = new Zeta.TreeSharp.Action(new ActionSucceedDelegate(FlagTagAsCompleted));
            children[0] = new Zeta.TreeSharp.Decorator(new CanRunDecoratorDelegate(CheckDistance), new Sequence(compositeArray));
            ActionDelegate actionDelegateMove = new ActionDelegate(MoveAwayFromKeywarden);
            Sequence sequenceblank = new Sequence(
                new Zeta.TreeSharp.Action(actionDelegateMove)
                );
            children[1] = sequenceblank;
            return new PrioritySelector(children);
        }

        private RunStatus MoveAwayFromKeywarden(object ret)
        {
            Vector3 NavTarget = Position;
            Vector3 MyPos = ZetaDia.Me.Position;
            if (Vector3.Distance(MyPos, NavTarget) > 250)
            {
                NavTarget = MathEx.CalculatePointFrom(MyPos, NavTarget, Vector3.Distance(MyPos, NavTarget) - 250);
            }

            // Move Away from KeyWarden
            Navigator.MoveTo(NavTarget);

            return RunStatus.Success;
        }

        private bool CheckDistance(object object_0)
        {
            if (KeyRun._distanceFromKeywarden >= fSafeDistance)
            {
                return true;
            }

            // return distance from point
            return (ZetaDia.Me.Position.Distance(Position) <= Math.Max(PathPrecision, Navigator.PathPrecision));
        }

        private void FlagTagAsCompleted(object object_0)
        {
            m_IsDone = true;
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }

        public override bool IsDone
        {
            get
            {
                if (IsActiveQuestStep)
                {
                    return m_IsDone;
                }
                return true;
            }
        }

        public Vector3 Position
        {
            get
            {
                vMainVector = new Vector3(X, Y, Z);
                return vMainVector.Value;
            }
        }

        [XmlAttribute("pathPrecision")]
        public float PathPrecision
        {
            get
            {
                return fPathPrecision;
            }
            set
            {
                fPathPrecision = value;
            }
        }


        [XmlAttribute("x")]
        public float X
        {
            get
            {
                return fPosX;
            }
            set
            {
                fPosX = value;
            }
        }

        [XmlAttribute("y")]
        public float Y
        {
            get
            {
                return fPosY;
            }
            set
            {
                fPosY = value;
            }
        }

        [XmlAttribute("z")]
        public float Z
        {
            get
            {
                return fPosZ;
            }
            set
            {
                fPosZ = value;
            }
        }

        [XmlAttribute("safeDistance")]
        public float fSafeDistance { get; set; }
    }

    public static class KeyCounter
    {
        private static readonly int[] keySNO = { 364694, 364695, 364696, 364697 };
        private static int[] keys = { 0, 0, 0, 0 };

        private static bool IsKeySno(int sno)
        {
            return keySNO.Any(k => k == sno);
        }

        private static void AddToKeyCount(int sno, int increment)
        {
            if (IsKeySno(sno))
            {
                keys[keySNO.IndexOf(sno)] += increment;
            }
        }

        public static void Refresh()
        {
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = 0;
            }

            foreach (ACDItem item in ZetaDia.Me.Inventory.StashItems)
            {
                if (IsKeySno(item.ActorSNO))
                {
                    AddToKeyCount(item.ActorSNO, item.ItemStackQuantity);
                }
            }

            foreach (ACDItem item in ZetaDia.Me.Inventory.Backpack)
            {
                if (IsKeySno(item.ActorSNO))
                {
                    AddToKeyCount(item.ActorSNO, item.ItemStackQuantity);
                }
            }
        }

        public static int GetNextActToRun()
        {
            int act = keys.IndexOf(keys.Min()) + 1;

            if (act == keys.IndexOf(keys.Max()) + 1)
            {
                Random rndAct = new Random(int.Parse(Guid.NewGuid().ToString().Substring(0, 8), NumberStyles.HexNumber));
                act = (rndAct.Next(3)) + 1;
            }

            return act;
        }

        public static void PrintStatistics()
        {
            Logger.Log(string.Format("Key Counts: Act 1 => {0},  Act 2 => {1},  Act 3 => {2}, Act 4 => {3}", keys[0], keys[1], keys[2], keys[3]));
        }

    }

    // KeyRunProfile
    [XmlElement("KeyRunProfile")]
    public class KeyRunProfile : ProfileBehavior
    {
        private bool m_IsDone = false;
        public override bool IsDone
        {
            get
            {
                return m_IsDone;
            }
        }

        protected override Composite CreateBehavior()
        {
            return new Zeta.TreeSharp.Action(ret =>
            {
                ZetaDia.Actors.Update();
                KeyCounter.Refresh();
                KeyCounter.PrintStatistics();

                // Choose Act with least amount of Keys
                int act = KeyCounter.GetNextActToRun();

                string sThisProfileString = string.Empty;
                Log(string.Format("Loading act {0}", act));
                switch (act)
                {
                    case 1:
                        sThisProfileString = Act1Profile;
                        break;
                    case 2:
                        sThisProfileString = Act2Profile;
                        break;
                    case 3:
                        sThisProfileString = Act3Profile;
                        break;
                    case 4:
                        sThisProfileString = Act4Profile;
                        break;
                    default:
                        break;
                }

                // See if there are multiple profile choices, if so split them up and pick a random one
                if (sThisProfileString.Contains("!"))
                {
                    string[] sProfileChoices;
                    sProfileChoices = sThisProfileString.Split(new string[] { "!" }, StringSplitOptions.None);
                    Random rndNum = new Random(Guid.NewGuid().GetHashCode());
                    int iChooseProfile = (rndNum.Next(sProfileChoices.Count())) - 1;
                    sThisProfileString = sProfileChoices[iChooseProfile];
                }
                // Now calculate our current path by checking the currently loaded profile
                string sCurrentProfilePath = Path.GetDirectoryName(Zeta.Bot.Settings.GlobalSettings.Instance.LastProfile);
                // And prepare a full string of the path, and the new .xml file name
                string sNextProfile = sCurrentProfilePath + @"\" + sThisProfileString;
                Log("Loading new profile.");
                Log(sNextProfile);
                ProfileManager.Load(sNextProfile);
                // A quick nap-time helps prevent some funny issues
                Thread.Sleep(3000);

                // Leaves Game
                Log("Exiting game to continue with next profile.");
                // Attempt to teleport to town first for a quicker exit
                int iSafetyLoops = 0;
                while (!ZetaDia.IsInTown)
                {
                    iSafetyLoops++;
                    ZetaDia.Me.UsePower(SNOPower.UseStoneOfRecall, ZetaDia.Me.Position, ZetaDia.Me.WorldDynamicId, -1);
                    Thread.Sleep(1000);
                    if (iSafetyLoops > 5)
                        break;
                }
                Thread.Sleep(1000);
                ZetaDia.Service.Party.LeaveGame();

                // Wait for 10 second log out timer if not in town, else wait for 3 seconds instead
                Thread.Sleep(!ZetaDia.IsInTown ? 10000 : 3000);

                m_IsDone = true;
            });
        }

        [XmlAttribute("act1profile")]
        public string Act1Profile
        {
            get;
            set;
        }
        [XmlAttribute("act2profile")]
        public string Act2Profile
        {
            get;
            set;
        }
        [XmlAttribute("act3profile")]
        public string Act3Profile
        {
            get;
            set;
        }
        [XmlAttribute("act4profile")]
        public string Act4Profile
        {
            get;
            set;
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }

        private void Log(string message, LogLevel logLevel = LogLevel.Info)
        {
            Logger.Log(message);
        }
    }

    // KeyRunChooseActProfile
    [XmlElement("KeyRunChooseActProfile")]
    public class KeyRunChooseActProfile : ProfileBehavior
    {
        private bool m_IsDone = false;
        public override bool IsDone
        {
            get { return m_IsDone; }
        }

        [XmlAttribute("profile")]
        public string ProfileName { get; set; }

        [XmlAttribute("exit")]
        public string Exit { get; set; }

        protected override Composite CreateBehavior()
        {
            return new Zeta.TreeSharp.Action(ret =>
                                                 {
                                                     // Set Exit Game
                                                     KeyRun.ChooseActProfileExitGame = Exit != null && Exit.ToLower() == "true";
                                                     // Now calculate our current path by checking the currently loaded profile
                                                     string sCurrentProfilePath = Path.GetDirectoryName(Zeta.Bot.Settings.GlobalSettings.Instance.LastProfile);
                                                     // And prepare a full string of the path, and the new .xml file name
                                                     string sNextProfile = sCurrentProfilePath + @"\" + ProfileName;
                                                     KeyRun.ChooseActProfile = sNextProfile;

                                                     m_IsDone = true;
                                                 });
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }

        private void Log(string message, LogLevel logLevel = LogLevel.Info)
        {
            Logger.Log(message);
        }
    }

    // KeyRunSetWardenDeathHP
    [XmlElement("KeyRunSetWardenDeathHP")]
    public class KeyRunSetWardenDeathHP : ProfileBehavior
    {
        private bool m_IsDone = false;
        public override bool IsDone
        {
            get { return m_IsDone; }
        }

        [XmlAttribute("hitpoints")]
        public float KeyWardenHP { get; set; }

        protected override Composite CreateBehavior()
        {
            return new Zeta.TreeSharp.Action(ret =>
                                                 {
                                                     Log("The KeyRunSetWardenDeathHP is deprecated. Change the value in the KeyRun plugin config instead!");
                                                     m_IsDone = true;
                                                 });
        }

        public override void ResetCachedDone()
        {
            m_IsDone = false;
            base.ResetCachedDone();
        }

        private void Log(string message, LogLevel logLevel = LogLevel.Info)
        {
            Logger.Log(message);
        }
    }
}
