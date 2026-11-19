using System.Globalization;
using System.IO;
using System.Text;

namespace VGuns.Broadcast
{
    /// <summary>The three top-level camera angles the director AI chooses between.</summary>
    public enum CameraAngle
    {
        FirstPerson = 0,
        ThirdPerson = 1,
        Cinematic = 2
    }

    /// <summary>Concrete shots. Each belongs to exactly one CameraAngle.</summary>
    public enum ShotKind
    {
        FPV_Stabilized = 0,
        TPV_OverShoulder = 1,
        TPV_ThreatFocus = 2,
        CINE_Orbit = 3,
        CINE_WideEstablishing = 4,
        CINE_KillCam = 5,
        CINE_DeathCam = 6,
        CINE_LowHero = 7
    }

    public enum ShotSize { CloseUp, Medium, MediumWide, Wide }
    public enum ShotHeight { EyeLevel, High, Low, Overhead }
    public enum ShotMovement { HeadLocked, Follow, Orbit, Crane, Dolly }

    public class ShotSpec
    {
        public ShotKind Kind;
        public CameraAngle Angle;
        public string Label;
        public ShotSize Size;
        public ShotHeight Height;
        public ShotMovement Movement;
        public string Subject;
        public float FieldOfView;
        public float MinHold;
        public float MaxHold;
        public string Triggers;
        public string Purpose;
    }

    /// <summary>
    /// Camera taxonomy for VGuns. Angle (what the AI decides) -> Shot (how the rig frames it).
    /// The same table is written to every telemetry session as camera_taxonomy.csv and
    /// documented in Docs/CameraTaxonomy.md.
    /// </summary>
    public static class CameraTaxonomy
    {
        public static readonly ShotSpec[] Shots =
        {
            new ShotSpec { Kind = ShotKind.FPV_Stabilized, Angle = CameraAngle.FirstPerson, Label = "First person",
                Size = ShotSize.CloseUp, Height = ShotHeight.EyeLevel, Movement = ShotMovement.HeadLocked,
                Subject = "player view", FieldOfView = 80f, MinHold = 2f, MaxHold = 12f,
                Triggers = "steady aiming; active shooting; enemy close in front",
                Purpose = "Player's own view with head jitter and roll removed so viewers do not get motion sick" },

            new ShotSpec { Kind = ShotKind.TPV_OverShoulder, Angle = CameraAngle.ThirdPerson, Label = "Third person",
                Size = ShotSize.Medium, Height = ShotHeight.EyeLevel, Movement = ShotMovement.Follow,
                Subject = "player avatar", FieldOfView = 65f, MinHold = 2f, MaxHold = 12f,
                Triggers = "fast head movement; reloading; low health",
                Purpose = "Over-the-shoulder follow camera showing the player's body and surroundings" },

            new ShotSpec { Kind = ShotKind.TPV_ThreatFocus, Angle = CameraAngle.ThirdPerson, Label = "Third person - threat",
                Size = ShotSize.MediumWide, Height = ShotHeight.EyeLevel, Movement = ShotMovement.Follow,
                Subject = "player + nearest enemy", FieldOfView = 62f, MinHold = 2f, MaxHold = 10f,
                Triggers = "player hit; enemies within attack range",
                Purpose = "Behind the player, aimed at the nearest enemy so viewers see where danger comes from" },

            new ShotSpec { Kind = ShotKind.CINE_Orbit, Angle = CameraAngle.Cinematic, Label = "Cinematic - orbit",
                Size = ShotSize.MediumWide, Height = ShotHeight.High, Movement = ShotMovement.Orbit,
                Subject = "player avatar", FieldOfView = 60f, MinHold = 3f, MaxHold = 10f,
                Triggers = "downtime; no enemies; between waves",
                Purpose = "Slow orbit around the player during quiet moments" },

            new ShotSpec { Kind = ShotKind.CINE_WideEstablishing, Angle = CameraAngle.Cinematic, Label = "Cinematic - wide",
                Size = ShotSize.Wide, Height = ShotHeight.High, Movement = ShotMovement.Crane,
                Subject = "player + all enemies", FieldOfView = 70f, MinHold = 2.5f, MaxHold = 8f,
                Triggers = "wave started; many enemies alive",
                Purpose = "High wide shot that establishes the arena and enemy positions" },

            new ShotSpec { Kind = ShotKind.CINE_KillCam, Angle = CameraAngle.Cinematic, Label = "Cinematic - kill cam",
                Size = ShotSize.Medium, Height = ShotHeight.Low, Movement = ShotMovement.Dolly,
                Subject = "killed enemy + shooter", FieldOfView = 55f, MinHold = 1.5f, MaxHold = 4f,
                Triggers = "enemy killed; multi-kill",
                Purpose = "Side-on replay-style framing of the shooter and the enemy that just died" },

            new ShotSpec { Kind = ShotKind.CINE_DeathCam, Angle = CameraAngle.Cinematic, Label = "Cinematic - death cam",
                Size = ShotSize.Wide, Height = ShotHeight.Overhead, Movement = ShotMovement.Crane,
                Subject = "player death spot", FieldOfView = 60f, MinHold = 3f, MaxHold = 999f,
                Triggers = "player death",
                Purpose = "Crane up and away from the player's position when they die" },

            new ShotSpec { Kind = ShotKind.CINE_LowHero, Angle = CameraAngle.Cinematic, Label = "Cinematic - low hero",
                Size = ShotSize.Medium, Height = ShotHeight.Low, Movement = ShotMovement.Follow,
                Subject = "player avatar (front)", FieldOfView = 55f, MinHold = 2f, MaxHold = 6f,
                Triggers = "bullet time; intense moment",
                Purpose = "Low angle in front of the player looking up for dramatic moments" },
        };

        public static ShotSpec Get(ShotKind kind)
        {
            return Shots[(int)kind];
        }

        public static CameraAngle AngleOf(ShotKind kind)
        {
            return Shots[(int)kind].Angle;
        }

        public static string Code(CameraAngle angle)
        {
            switch (angle)
            {
                case CameraAngle.FirstPerson: return "FPV";
                case CameraAngle.ThirdPerson: return "TPV";
                default: return "CINE";
            }
        }

        public static bool TryParseCode(string code, out CameraAngle angle)
        {
            angle = CameraAngle.FirstPerson;
            if (string.IsNullOrEmpty(code)) return false;
            switch (code.Trim().ToUpperInvariant())
            {
                case "FPV": case "FIRST": case "0": angle = CameraAngle.FirstPerson; return true;
                case "TPV": case "THIRD": case "1": angle = CameraAngle.ThirdPerson; return true;
                case "CINE": case "CINEMATIC": case "2": angle = CameraAngle.Cinematic; return true;
            }
            return false;
        }

        public static string ShotName(ShotKind kind)
        {
            switch (kind)
            {
                case ShotKind.FPV_Stabilized: return "Stabilized";
                case ShotKind.TPV_OverShoulder: return "Over-shoulder";
                case ShotKind.TPV_ThreatFocus: return "Threat focus";
                case ShotKind.CINE_Orbit: return "Orbit";
                case ShotKind.CINE_WideEstablishing: return "Wide";
                case ShotKind.CINE_KillCam: return "Kill cam";
                case ShotKind.CINE_DeathCam: return "Death cam";
                default: return "Low hero";
            }
        }

        public static void WriteCsv(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("shot_id,angle,label,shot_size,height,movement,subject,fov,min_hold_s,max_hold_s,triggers,purpose");
            foreach (var s in Shots)
            {
                sb.Append(s.Kind).Append(',')
                  .Append(Code(s.Angle)).Append(',')
                  .Append(Csv.Escape(s.Label)).Append(',')
                  .Append(s.Size).Append(',')
                  .Append(s.Height).Append(',')
                  .Append(s.Movement).Append(',')
                  .Append(Csv.Escape(s.Subject)).Append(',')
                  .Append(s.FieldOfView.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.MinHold.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.MaxHold.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv.Escape(s.Triggers)).Append(',')
                  .Append(Csv.Escape(s.Purpose)).AppendLine();
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
