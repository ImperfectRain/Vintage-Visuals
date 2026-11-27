using System;
using System.Linq;
using System.Reflection;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// The four raw GL calls the mirror pass needs that IRenderAPI does not
    /// offer: depth function, front-face winding, clear depth value, and a
    /// depth-only clear. Reached through OpenTK by reflection, like the depth
    /// prepass's bridge - the project does not compile against OpenTK, the game
    /// owns that dependency. Null when any of them cannot be found, and the
    /// mirror pass then refuses to run rather than draw with wrong state.
    /// </summary>
    internal sealed class MirrorGl
    {
        public const int Less = 0x0201;
        public const int Always = 0x0207;
        public const int Cw = 0x0900;
        public const int Ccw = 0x0901;
        private const int DepthBufferBit = 0x00000100;

        private readonly MethodInfo _depthFunc, _frontFace, _clearDepth, _clear;
        private readonly Type _depthFuncType, _frontFaceType, _clearMaskType;

        private MirrorGl(MethodInfo depthFunc, MethodInfo frontFace, MethodInfo clearDepth, MethodInfo clear)
        {
            _depthFunc = depthFunc; _frontFace = frontFace; _clearDepth = clearDepth; _clear = clear;
            _depthFuncType = depthFunc.GetParameters()[0].ParameterType;
            _frontFaceType = frontFace.GetParameters()[0].ParameterType;
            _clearMaskType = clear.GetParameters()[0].ParameterType;
        }

        public static MirrorGl TryCreate(out string why)
        {
            why = null;
            try
            {
                Type gl = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" || t.FullName == "OpenTK.Graphics.OpenGL4.GL");
                if (gl == null) { why = "OpenTK GL type not found"; return null; }

                MethodInfo[] all = gl.GetMethods(BindingFlags.Public | BindingFlags.Static);
                MethodInfo depthFunc = all.FirstOrDefault(m => m.Name == "DepthFunc" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                MethodInfo frontFace = all.FirstOrDefault(m => m.Name == "FrontFace" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                MethodInfo clearDepth = all.FirstOrDefault(m => m.Name == "ClearDepth" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(double));
                MethodInfo clear = all.FirstOrDefault(m => m.Name == "Clear" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                if (depthFunc == null || frontFace == null || clearDepth == null || clear == null)
                {
                    why = "OpenTK GL.DepthFunc/FrontFace/ClearDepth/Clear not found";
                    return null;
                }
                return new MirrorGl(depthFunc, frontFace, clearDepth, clear);
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return null;
            }
        }

        public void DepthFunc(int func) { _depthFunc.Invoke(null, new[] { Enum.ToObject(_depthFuncType, func) }); }
        public void FrontFace(int mode) { _frontFace.Invoke(null, new[] { Enum.ToObject(_frontFaceType, mode) }); }

        /// <summary>Clears the bound depth buffer (inside the scissor) to <paramref name="value"/>, then restores the clear value to 1.</summary>
        public void ClearDepthTo(double value)
        {
            _clearDepth.Invoke(null, new object[] { value });
            _clear.Invoke(null, new[] { Enum.ToObject(_clearMaskType, DepthBufferBit) });
            _clearDepth.Invoke(null, new object[] { 1.0 });
        }
    }
}
