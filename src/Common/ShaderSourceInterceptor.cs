using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.Common
{
    /// <summary>
    /// Intercepts vanilla GLSL between "read from disk" and "compile", and runs
    /// it through the <see cref="ShaderPatcher"/>.
    ///
    /// This is the mod's ONLY dependency on Vintage Story internals, and it is
    /// kept deliberately thin. There is no public API for rewriting a vanilla
    /// shader: <see cref="IShaderProgram.Compile"/> re-reads file shaders from
    /// the asset, so mutating <see cref="IShader.Code"/> from outside is
    /// overwritten. Hooking the loader is the approach every Vintage Story
    /// shader mod uses.
    ///
    /// Coupling is minimised three ways:
    ///  - The internal type is resolved by name at runtime, so the mod does not
    ///    reference VintagestoryLib and a rename produces a logged error rather
    ///    than a build failure or a crash.
    ///  - The target method is located by shape (two parameters, second is
    ///    EnumShaderType) rather than by exact signature, which would otherwise
    ///    name an internal type.
    ///  - The patch argument is taken positionally as <c>__0</c> and typed as
    ///    <c>object</c>, so neither the parameter's name nor its declared type
    ///    can break the injection. It is used only through the public
    ///    <see cref="IShaderProgram"/> interface.
    /// </summary>
    public sealed class ShaderSourceInterceptor
    {
        private const string ShaderRegistryTypeName = "Vintagestory.Client.NoObf.ShaderRegistry";
        private const string LoadShaderMethodName = "LoadShader";

        // Harmony patches must be static, so the collaborators they need are
        // static too. There is exactly one client and therefore one instance of
        // this mod per process, which is what makes that safe.
        private static ShaderPatcher _patcher;
        private static ILogger _logger;
        private static bool _dumpShaders;

        private readonly Harmony _harmony;
        private readonly ILogger _instanceLogger;
        private bool _installed;

        public ShaderSourceInterceptor(string harmonyId, ShaderPatcher patcher, ILogger logger)
        {
            _harmony = new Harmony(harmonyId);
            _instanceLogger = logger;
            _patcher = patcher;
            _logger = logger;
        }

        /// <summary>Turns the post-patch GLSL dump on or off at runtime.</summary>
        public void SetShaderDumpEnabled(bool enabled)
        {
            _dumpShaders = enabled;
        }

        /// <summary>
        /// Installs the hook. Returns false — having logged why — if the game's
        /// internals no longer look the way this expects. The caller must then
        /// treat every shader-patch-dependent subsystem as unavailable.
        /// </summary>
        public bool Install()
        {
            if (_installed) return true;

            Type registryType = AccessTools.TypeByName(ShaderRegistryTypeName);
            if (registryType == null)
            {
                _instanceLogger.Error("[VintageVisuals] CRITICAL cannot find " + ShaderRegistryTypeName +
                    ". The game's internals have changed; no shader patches can be applied. " +
                    "Every visual subsystem is disabled, but the game will run normally.");
                return false;
            }

            MethodInfo target = FindLoadShaderMethod(registryType);
            if (target == null)
            {
                _instanceLogger.Error("[VintageVisuals] CRITICAL cannot find a suitable " + LoadShaderMethodName +
                    "(.., EnumShaderType) on " + ShaderRegistryTypeName + ". No shader patches can be applied. " +
                    "Every visual subsystem is disabled, but the game will run normally.");
                return false;
            }

            try
            {
                var postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(ShaderSourceInterceptor), nameof(LoadShaderPostfix)));

                _harmony.Patch(target, postfix: postfix);
                _installed = true;

                _instanceLogger.Notification("[VintageVisuals] shader source interceptor installed on " +
                                             target.DeclaringType?.Name + "." + target.Name);
                return true;
            }
            catch (Exception ex)
            {
                _instanceLogger.Error("[VintageVisuals] CRITICAL failed to install the shader source interceptor. " +
                                      "Every visual subsystem is disabled, but the game will run normally.");
                _instanceLogger.LogException(EnumLogType.Error, ex);
                return false;
            }
        }

        public void Uninstall()
        {
            if (!_installed) return;

            try
            {
                _harmony.UnpatchAll(_harmony.Id);
            }
            catch (Exception ex)
            {
                _instanceLogger.Warning("[VintageVisuals] failed to remove Harmony patches: " + ex.Message);
            }

            _installed = false;
            _patcher = null;
            _logger = null;
        }

        /// <summary>
        /// Finds LoadShader by shape rather than exact signature: its first
        /// parameter is an internal type this assembly deliberately cannot name.
        /// </summary>
        private static MethodInfo FindLoadShaderMethod(Type registryType)
        {
            return registryType
                .GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    m.Name == LoadShaderMethodName &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(EnumShaderType));
        }

        /// <summary>
        /// Runs after the game has loaded a shader's source but before it is
        /// compiled. <c>__0</c> is Harmony's positional accessor for the first
        /// parameter — see the class comment for why it is not named or typed.
        /// </summary>
        public static void LoadShaderPostfix(object __0, EnumShaderType __1)
        {
            ShaderPatcher patcher = _patcher;
            ILogger logger = _logger;
            if (patcher == null) return;

            try
            {
                var program = __0 as IShaderProgram;
                if (program == null) return;

                string extension = ExtensionFor(__1);
                if (extension == null) return;

                IShader shader = SelectShader(program, __1);
                if (shader == null || string.IsNullOrEmpty(shader.Code)) return;

                string filename = program.PassName + extension;

                string original = shader.Code;
                string patched = patcher.Patch(filename, original);

                // Only write back when the source actually changed.
                //
                // This hook sees EVERY shader the game loads, and the vast
                // majority have no patch targeting them at all. Assigning
                // IShader.Code is not obviously free — it is a property on an
                // unstable internal type, and whatever bookkeeping its setter
                // does would then run for every shader in the game because of a
                // mod that wanted to change two of them. Writing only on a real
                // change keeps this mod's footprint to the files it actually
                // edits, which is what it always claimed to have.
                if (!string.Equals(original, patched, StringComparison.Ordinal))
                {
                    shader.Code = patched;
                }

                if (_dumpShaders) DumpShader(filename, shader, logger);
            }
            catch (Exception ex)
            {
                // The caller is the game's shader loader. Throwing here would
                // take the client down over a cosmetic mod.
                if (logger != null)
                {
                    logger.Error("[VintageVisuals] unexpected error while patching shader source; " +
                                 "leaving this shader unpatched.");
                    logger.LogException(EnumLogType.Error, ex);
                }
            }
        }

        private static IShader SelectShader(IShaderProgram program, EnumShaderType type)
        {
            switch (type)
            {
                case EnumShaderType.FragmentShader: return program.FragmentShader;
                case EnumShaderType.VertexShader: return program.VertexShader;
                // EnumShaderType.GeometryShaderExt shares GeometryShader's
                // numeric value, so it must not appear as a separate case.
                case EnumShaderType.GeometryShader: return program.GeometryShader;
                default: return null;
            }
        }

        private static string ExtensionFor(EnumShaderType type)
        {
            switch (type)
            {
                case EnumShaderType.FragmentShader: return ".fsh";
                case EnumShaderType.VertexShader: return ".vsh";
                case EnumShaderType.GeometryShader: return ".gsh";
                default: return null;
            }
        }

        /// <summary>
        /// Writes the post-patch source to VintagestoryData/ShaderDebug/.
        /// The prefix code is prepended the way the game will see it, so the
        /// dumped file is what actually reaches the GLSL compiler — line
        /// numbers in driver errors line up with this file, not with the
        /// vanilla asset.
        /// </summary>
        private static void DumpShader(string filename, IShader shader, ILogger logger)
        {
            try
            {
                string directory = Path.Combine(GamePaths.DataPath, "ShaderDebug");
                Directory.CreateDirectory(directory);

                string body = shader.Code;
                string prefix = shader.PrefixCode;

                if (!string.IsNullOrEmpty(prefix))
                {
                    // #version must stay the first directive, so the prefix goes
                    // after it rather than at the top of the file.
                    int versionIndex = body.IndexOf("#version", StringComparison.Ordinal);
                    int insertAt = versionIndex < 0 ? 0 : body.IndexOf('\n', versionIndex) + 1;
                    if (insertAt > 0) body = body.Insert(insertAt, prefix);
                    else body = prefix + body;
                }

                File.WriteAllText(Path.Combine(directory, filename), body);
            }
            catch (Exception ex)
            {
                if (logger != null) logger.Warning("[VintageVisuals] could not dump " + filename + ": " + ex.Message);
            }
        }
    }
}
