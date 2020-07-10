using Unity.Collections;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Rendering
{
    internal class ComputeBufferWrapper<DataType> where DataType : struct
    {
        ComputeBuffer m_Buffer;
        ComputeShader m_Shader;
        int m_PropertyID;

        public int BufferSize { get; private set; }

        public ComputeBufferWrapper(string name, int size)
        {
            BufferSize = size;
            m_PropertyID = Shader.PropertyToID(name);
            m_Buffer = new ComputeBuffer(size, UnsafeUtility.SizeOf<DataType>(), ComputeBufferType.Default);
        }

        public ComputeBufferWrapper(string name, int size, ComputeShader shader) : this(name, size)
        {
            Debug.Assert(shader != null);
            m_Shader = shader;
        }

        public void Resize(int newSize)
        {
            BufferSize = newSize;
            m_Buffer.Dispose();
            m_Buffer = new ComputeBuffer(newSize, UnsafeUtility.SizeOf<DataType>(), ComputeBufferType.Default);
        }

        public void SetData(NativeArray<DataType> data, int nativeBufferStartIndex, int computeBufferStartIndex, int count)
        {
            m_Buffer.SetData(data, nativeBufferStartIndex, computeBufferStartIndex, count);
        }

        public void PushDataToGlobal()
        {
            Debug.Assert(m_Buffer.count > 0);
            Debug.Assert(m_Buffer.IsValid());
            Shader.SetGlobalBuffer(m_PropertyID, m_Buffer);
        }

        public void PushDataToKernel(int kernelIndex)
        {
            Debug.Assert(m_Buffer.count > 0 && m_Shader != null);
            Debug.Assert(m_Buffer.IsValid());
            m_Shader.SetBuffer(kernelIndex, m_PropertyID, m_Buffer);
        }

        public void Destroy()
        {
            BufferSize = -1;
            m_PropertyID = -1;
            m_Buffer.Dispose();
            m_Shader = null;
        }
    }
}
