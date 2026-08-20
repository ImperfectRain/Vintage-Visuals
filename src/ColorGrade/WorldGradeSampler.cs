using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.ColorGrade
{
    /// <summary>
    /// Reads the world and hands back a <see cref="GradeContext"/>.
    ///
    /// The whole point of the split is that this is the only file that knows
    /// about the game. Everything about what the numbers MEAN lives in
    /// GradeStack, where it can be driven through every combination without a
    /// client - and everything about where they come from lives here, where it
    /// can be wrong in exactly one place when the API moves under us.
    ///
    /// Every lookup is guarded and every failure falls back to the neutral
    /// value for that field, so a world query that starts throwing costs the
    /// grade its awareness of one thing rather than costing the player their
    /// screen.
    /// </summary>
    public sealed class WorldGradeSampler
    {
        /// <summary>
        /// Blocks below sea level at which the underground look is at full
        /// strength. Chosen so a cellar barely registers and a real cave system
        /// is well into it.
        /// </summary>
        private const float FullDepthBlocks = 45f;

        private readonly ICoreClientAPI _capi;

        private float _rain;
        private float _cloudCover;
        private float _temperature = GradeStack.TemperateCelsius;
        private float _rainfall = 0.5f;

        public WorldGradeSampler(ICoreClientAPI capi)
        {
            _capi = capi;
        }

        /// <summary>
        /// The last climate reading, kept so the per-frame path never has to do
        /// a climate lookup. Climate does not change in a hurry and the easing
        /// between readings is what the player sees.
        /// </summary>
        public void SampleClimate()
        {
            try
            {
                IClientPlayer player = _capi.World?.Player;
                if (player?.Entity == null) return;

                BlockPos pos = player.Entity.Pos.AsBlockPos;
                ClimateCondition climate = _capi.World.BlockAccessor.GetClimateAt(pos);
                if (climate == null) return;

                _rain = GameMath.Clamp(climate.Rainfall, 0f, 1f);
                _temperature = climate.Temperature;

                // WorldgenRainfall, not Rainfall. The two are different
                // questions: Rainfall is how hard it is raining right now, and
                // this influence is asking what KIND of place this is - a
                // rainforest in a dry spell is still a rainforest, and grading
                // it as a desert until the next shower would be worse than not
                // grading it at all.
                _rainfall = GameMath.Clamp(climate.WorldgenRainfall, 0f, 1f);
            }
            catch (Exception)
            {
                // Left at the last good reading rather than reset. A lookup
                // that throws is a reason to stop tracking, not a reason to
                // claim the player has moved to a temperate plain.
            }

            try
            {
                if (_capi.Ambient != null)
                {
                    _cloudCover = GameMath.Clamp(_capi.Ambient.BlendedCloudDensity * 2f, 0f, 1f);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Everything else, which is cheap enough to read as often as the grade
        /// is stepped.
        /// </summary>
        public GradeContext Read()
        {
            IClientPlayer player = _capi.World?.Player;
            if (player?.Entity == null) return GradeContext.Neutral;

            BlockPos pos = player.Entity.Pos.AsBlockPos;

            return new GradeContext(
                DayLight(),
                _rain,
                _cloudCover,
                SkyExposure(pos),
                Depth(pos),
                _temperature,
                _rainfall,
                Underwater(player));
        }

        private float DayLight()
        {
            var calendar = _capi.World?.Calendar;
            if (calendar == null) return 1f;

            return GameMath.Clamp(calendar.DayLightStrength, 0f, 1f);
        }

        /// <summary>
        /// How much sky can see this spot, 0..1.
        ///
        /// OnlySunLight rather than MaxTimeOfDayLight, which is what the eye
        /// adaptation uses. The two are asking different questions: adaptation
        /// wants to know how bright it is here right now, and would happily
        /// count a torch; this wants to know whether the player is indoors, and
        /// a torch is evidence of nothing.
        /// </summary>
        private float SkyExposure(BlockPos pos)
        {
            try
            {
                int level = _capi.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.OnlySunLight);
                float max = Math.Max(1, _capi.World.SunBrightness);
                return GameMath.Clamp(level / max, 0f, 1f);
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        private float Depth(BlockPos pos)
        {
            try
            {
                float below = _capi.World.SeaLevel - pos.Y;
                return GameMath.Clamp(below / FullDepthBlocks, 0f, 1f);
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>
        /// Whether the camera is submerged.
        ///
        /// Swimming rather than a block lookup at the eye. It is the game's own
        /// answer to the same question, it already handles the cases a naive
        /// lookup gets wrong, and being one block out at the surface costs a
        /// fraction of a second of tint on a value that is eased over seconds
        /// anyway.
        /// </summary>
        private float Underwater(IClientPlayer player)
        {
            try
            {
                return player.Entity.Swimming ? 1f : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }
    }
}
