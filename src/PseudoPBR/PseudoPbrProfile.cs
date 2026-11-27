using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// D99. What the profiler measures for the PBR material path and the forest effects. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class PseudoPbrProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("terrainbase", "Terrain base varyings")
             .Groups(PseudoPbrSubsystem.TerrainBaseGroupName)
             .ActiveWhen(c => c.PseudoPBR.Enabled);

            r.Declare("terrainmaterial", "Terrain material (PBR shading, foliage light, gusts, damp, torch specular)")
             .Groups(PseudoPbrSubsystem.TerrainMaterialGroupName)
             .SettingsUnder("PseudoPBR.")
             .Sweep("pbr_normalstrength", 0f, 0.5f, 1f)
             .Sweep("pbr_specularstrength", 0f, 0.5f, 1f)
             .Sweep("pbr_detaildistance")
             .Sweep("pbr_foliage", 0f, 1f)
             .Sweep("pbr_gusts", 0f, 1f)
             .Sweep("pbr_damp", 0f, 1f)
             .Sweep("pbr_blocklight", 0f, 1f)
             .Renderers("vintagevisuals-pbr")
             .Scenes("F", "V", "C")
             .Note("the whole terrain shader; '.vvbench features' mutes its parts with the code kept, this measures them compiled")
             .ActiveWhen(c => c.PseudoPBR.Enabled);

            r.Declare("terraindebug", "Terrain debug views")
             .Groups(PseudoPbrSubsystem.TerrainDebugGroupName)
             .Requires(PseudoPbrSubsystem.TerrainMaterialGroupName)
             .Settings("PseudoPBR.DebugView")
             .Scenes("F")
             .Note("compiled in only while a debug view is selected (D90)");

            r.Declare("canopylight", "Sunflecks, dapple and canopy shade")
             .Settings("PseudoPBR.SunDapple", "PseudoPBR.SunFlecks", "PseudoPBR.CanopyRadius", "PseudoPBR.MoonFlecks")
             .Sweep("pbr_sunflecks", 0f, 0.5f, 1f)
             .Sweep("pbr_canopyradius")
             .Scenes("F")
             .Note("runs inside the terrain material; its compiled cost is part of pbrterrainmaterial")
             .ActiveWhen(c => c.PseudoPBR.Enabled);

            r.Declare("beams", "Light shafts, air beams and dust motes")
             .Groups(PseudoPbrSubsystem.TerrainFastBeamsGroupName)
             .Requires(PseudoPbrSubsystem.TerrainMaterialGroupName)
             .Settings("PseudoPBR.SunShafts", "PseudoPBR.FastBeams", "PseudoPBR.DustMotes")
             .Sweep("pbr_shafts", 0f, 0.5f, 1f)
             .Sweep("pbr_fastbeams")
             .Sweep("pbr_motes", 0f, 1f)
             .Renderers("vintagevisuals-beams", "vintagevisuals-motes")
             .GpuSpans("vv.beams")
             .Scenes("F")
             .Note("the pbrterrainfastbeams group REMOVES the terrain march while the fast pass draws, so its 'compiled cost' should be negative; '.vvbench beams' compares the two paths")
             .ActiveWhen(c => c.PseudoPBR.Enabled && c.PseudoPBR.SunShafts > 0.001f);

            r.Declare("fallingleaves", "Falling leaves")
             .Settings("PseudoPBR.FallingLeaves")
             .Sweep("pbr_leaves", 0f, 0.5f, 1f)
             .Renderers("vintagevisuals-leaves")
             .GpuSpans("vv.leaves")
             .Scenes("F")
             .Note("'.vvbench leaves' also compares the old 20-block reach")
             .ActiveWhen(c => c.PseudoPBR.Enabled && c.PseudoPBR.FallingLeaves > 0.001f);

            r.Declare("forestlife", "Forest life: leaf drips, pollen, fireflies")
             .Settings("PseudoPBR.LeafDrips", "PseudoPBR.Pollen", "PseudoPBR.Fireflies")
             .Sweep("pbr_fireflies", 0f, 1f)
             .Sweep("pbr_pollen", 0f, 1f)
             .Renderers("vintagevisuals-forestlife")
             .Scenes("F")
             .ActiveWhen(c => c.PseudoPBR.Enabled);

            r.Declare("entitypbr", "Entity PBR lighting")
             .Groups(PseudoPbrSubsystem.EntityGroupName)
             .Settings("PseudoPBR.EntityLighting", "PseudoPBR.EntityRoughness", "PseudoPBR.EntitySpecular")
             .Sweep("pbr_entityspecular", 0f, 1f)
             .Scenes("E")
             .Note("cost scales with entities on screen; use a scene with 20+")
             .ActiveWhen(c => c.PseudoPBR.EntityLighting);

            r.Declare("particlepbr", "Particle PBR lighting")
             .Groups(PseudoPbrSubsystem.ParticleGroupName)
             .Settings("PseudoPBR.ParticleLighting", "PseudoPBR.ParticleSpecular")
             .Sweep("pbr_particlespecular", 0f, 1f)
             .Scenes("R")
             .Note("rain is the heaviest particle load; measure in a storm scene")
             .ActiveWhen(c => c.PseudoPBR.ParticleLighting);

            r.Declare("legacypbr", "Legacy terrain path (pseudopbr, pseudopbrtopsoil)")
             .Groups(PseudoPbrSubsystem.GroupName, PseudoPbrSubsystem.TopsoilGroupName)
             .Note("compiled only on builds with the legacy terrain path switched on")
             .ActiveWhen(c => PseudoPbrSubsystem.TerrainShaderPatchesEnabled && c.PseudoPBR.Enabled);
        }
    }
}
