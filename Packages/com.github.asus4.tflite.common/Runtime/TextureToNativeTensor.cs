using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace TensorFlowLite
{
    /// <summary>
    /// Converts Texture to Tensor with arbitrary matrix transformation
    /// then return it as a NativeArray (NHWC layout)
    /// </summary>
    public class TextureToNativeTensor : IDisposable
    {
        [Serializable]
        public class Options
        {
            public ComputeShader compute = null;
            public int kernel = 0;
            public int width = 0;
            public int height = 0;
            public int channels = 0;
            public Type inputType = typeof(float);
        }

        private static readonly Lazy<ComputeShader> DefaultComputeShaderFloat32 = new(() =>
        {
            const string path = "com.github.asus4.tflite.common/TextureToNativeTensorFloat32";
            return Resources.Load<ComputeShader>(path);
        });


        private static readonly int _InputTex = Shader.PropertyToID("_InputTex");
        private static readonly int _OutputTex = Shader.PropertyToID("_OutputTex");
        private static readonly int _OutputTensor = Shader.PropertyToID("_OutputTensor");
        private static readonly int _OutputSize = Shader.PropertyToID("_OutputSize");
        private static readonly int _TransformMatrix = Shader.PropertyToID("_TransformMatrix");

        private static readonly Matrix4x4 PopMatrix = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0));
        private static readonly Matrix4x4 PushMatrix = Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0));

        private readonly ComputeShader compute;
        private readonly int kernel;
        private readonly int width;
        private readonly int height;
        private readonly int channels;

        private readonly RenderTexture texture;
        private readonly GraphicsBuffer tensorBuffer;
        private NativeArray<byte> tensor;

        public RenderTexture Texture => texture;
        public Matrix4x4 TransformMatrix { get; private set; } = Matrix4x4.identity;

        public TextureToNativeTensor(Options options)
        {
            compute = options.compute != null
                ? options.compute
                : DefaultComputeShaderFloat32.Value;
            kernel = options.kernel;
            width = options.width;
            height = options.height;
            channels = options.channels;

            Assert.IsTrue(kernel >= 0, $"Kernel must be set");
            Assert.IsTrue(width > 0, $"Width must be greater than 0");
            Assert.IsTrue(height > 0, $"Height must be greater than 0");
            Assert.IsTrue(channels > 0 && channels <= 4, $"Channels must be 1 to 4");
            Assert.IsTrue(UnsafeUtility.IsUnmanaged(options.inputType), "Type must be unmanaged");

            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = true,
                useMipMap = false,
                depthBufferBits = 0,
            };
            texture = new RenderTexture(desc);
            texture.Create();

            int length = width * height * channels;
            int stride = UnsafeUtility.SizeOf(options.inputType);
            tensorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, length, stride);
            tensor = new NativeArray<byte>(length * stride, Allocator.Persistent);

            // Set constant values
            compute.SetInts(_OutputSize, width, height);
            compute.SetBuffer(kernel, _OutputTensor, tensorBuffer);
            compute.SetTexture(kernel, _OutputTex, texture, 0);
        }

        public void Dispose()
        {
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            // tensorData.Dispose();
            tensorBuffer.Dispose();
        }

        public NativeArray<byte> Transform(Texture input, in Matrix4x4 t)
        {
            TransformMatrix = t;
            compute.SetTexture(kernel, _InputTex, input, 0);
            compute.SetMatrix(_TransformMatrix, t);
            compute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

            // TODO: Implement async version
            var request = AsyncGPUReadback.RequestIntoNativeArray(ref tensor, tensorBuffer, (request) =>
            {
                if (request.hasError)
                {
                    Debug.LogError("GPU readback error detected.");
                    return;
                }
            });
            request.WaitForCompletion();
            return tensor;
        }

        public NativeArray<byte> Transform(Texture input, AspectMode aspectMode)
        {
            return Transform(input, GetAspectScaledMatrix(input, aspectMode));
        }

        public Matrix4x4 GetAspectScaledMatrix(Texture input, AspectMode aspectMode)
        {
            float srcAspect = (float)input.width / input.height;
            float dstAspect = (float)width / height;
            Vector2 scale = GetAspectScale(srcAspect, dstAspect, aspectMode);
            return PopMatrix * Matrix4x4.Scale(new Vector3(scale.x, scale.y, 1)) * PushMatrix;
        }

        public static Vector2 GetAspectScale(float srcAspect, float dstAspect, AspectMode mode)
        {
            bool isSrcWider = srcAspect > dstAspect;
            return (mode, isSrcWider) switch
            {
                (AspectMode.None, _) => new Vector2(1, 1),
                (AspectMode.Fit, true) => new Vector2(1, srcAspect / dstAspect),
                (AspectMode.Fit, false) => new Vector2(dstAspect / srcAspect, 1),
                (AspectMode.Fill, true) => new Vector2(dstAspect / srcAspect, 1),
                (AspectMode.Fill, false) => new Vector2(1, srcAspect / dstAspect),
                _ => throw new Exception("Unknown aspect mode"),
            };
        }
    }
}
