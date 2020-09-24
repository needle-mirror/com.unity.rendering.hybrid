using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering.Tests
{
    public class SparseUploaderTests
    {
        struct ExampleStruct
        {
            public int someData;
        }


        private ComputeBuffer buffer;
        private SparseUploader uploader;
        private void Setup<T>(int count) where T : struct
        {
            buffer = new ComputeBuffer(count, UnsafeUtility.SizeOf<T>());
            uploader = new SparseUploader(buffer);
        }

        private void Setup<T>(T[] initialData) where T : struct
        {
            buffer = new ComputeBuffer(initialData.Length, UnsafeUtility.SizeOf<T>());
            buffer.SetData(initialData);
            uploader = new SparseUploader(buffer);
        }

        private void Teardown()
        {
            uploader.Dispose();
            buffer.Dispose();
        }

        private float4x4 GenerateTestMatrix(int i)
        {
            var trans = float4x4.Translate(new float3(i * 0.2f, -i * 0.4f, math.cos(i * math.PI * 0.02f)));
            var rot = float4x4.EulerXYZ(i * 0.1f, math.PI * 0.5f, -i * 0.3f);
            return math.mul(trans, rot);
        }

        private float4x4 ExpandMatrix(float3x4 mat)
        {
            return new float4x4(
                new float4(mat.c0.x, mat.c0.y, mat.c0.z, 0.0f),
                new float4(mat.c1.x, mat.c1.y, mat.c1.z, 0.0f),
                new float4(mat.c2.x, mat.c2.y, mat.c2.z, 0.0f),
                new float4(mat.c3.x, mat.c3.y, mat.c3.z, 1.0f));
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void ReplaceBuffer()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }


            var initialData = new ExampleStruct[64];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = i };

            Setup(initialData);

            var newBuffer = new ComputeBuffer(initialData.Length * 2, UnsafeUtility.SizeOf<ExampleStruct>());

            uploader.ReplaceBuffer(newBuffer, true);
            buffer.Dispose();
            buffer = newBuffer;

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(i, resultingData[i].someData);

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void NoUploads()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            Setup<float>(1);

            var tsu = uploader.Begin(0, 0, 0);
            uploader.EndAndCommit(tsu);

            tsu = uploader.Begin(1024, 1024, 1);
            uploader.EndAndCommit(tsu);

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void SmallUpload()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[64];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = 0 };

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var totalSize = structSize * initialData.Length;
            var tsu = uploader.Begin(totalSize, structSize, initialData.Length);
            for (int i = 0; i < initialData.Length; ++i)
                tsu.AddUpload(new ExampleStruct { someData = i }, i * 4);
            uploader.EndAndCommit(tsu);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(i, resultingData[i].someData);

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void BasicUploads()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[1024];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct {someData = i};

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var totalSize = structSize * initialData.Length;
            var tsu = uploader.Begin(totalSize, totalSize, initialData.Length);
            tsu.AddUpload(new ExampleStruct {someData = 7}, 4);
            uploader.EndAndCommit(tsu);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            Assert.AreEqual(0, resultingData[0].someData);
            Assert.AreEqual(7, resultingData[1].someData);
            Assert.AreEqual(2, resultingData[2].someData);

            tsu = uploader.Begin(structSize, structSize, 1);
            tsu.AddUpload(new ExampleStruct {someData = 13}, 8);
            uploader.EndAndCommit(tsu);

            buffer.GetData(resultingData);

            Assert.AreEqual(0, resultingData[0].someData);
            Assert.AreEqual(7, resultingData[1].someData);
            Assert.AreEqual(13, resultingData[2].someData);
            Assert.AreEqual(3, resultingData[3].someData);

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public unsafe void BigUploads()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[4 * 1024];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct {someData = i};

            Setup(initialData);

            var newData = new ExampleStruct[312];
            for (int i = 0; i < newData.Length; ++i)
                newData[i] = new ExampleStruct {someData = i + 3000};

            var newData2 = new ExampleStruct[316];
            for (int i = 0; i < newData2.Length; ++i)
                newData2[i] = new ExampleStruct {someData = i + 4000};

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var totalSize = structSize * (newData.Length + newData2.Length);
            var tsu = uploader.Begin(totalSize, totalSize, initialData.Length);
            fixed(void* ptr = newData)
            tsu.AddUpload(ptr, newData.Length * 4, 512 * 4);
            fixed(void* ptr2 = newData2)
            tsu.AddUpload(ptr2, newData2.Length * 4, 1136 * 4);
            uploader.EndAndCommit(tsu);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
            {
                if (i < 512)
                    Assert.AreEqual(i, resultingData[i].someData);
                else if (i < 824)
                    Assert.AreEqual(i - 512 + 3000, resultingData[i].someData);
                else if (i < 1136)
                    Assert.AreEqual(i, resultingData[i].someData);
                else if (i < 1452)
                    Assert.AreEqual(i - 1136 + 4000, resultingData[i].someData);
                else
                    Assert.AreEqual(i, resultingData[i].someData);
            }

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void SplatUpload()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[64];

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = 0 };

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var tsu = uploader.Begin(structSize, structSize, 1);
            tsu.AddUpload(new ExampleStruct { someData = 1 }, 0, 64);
            uploader.EndAndCommit(tsu);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(1, resultingData[i].someData);

            Teardown();
        }

        struct UploadJob : IJobParallelFor
        {
            public ThreadedSparseUploader uploader;

            public void Execute(int index)
            {
                uploader.AddUpload(new ExampleStruct {someData = index}, index * 4);
            }
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void UploadFromJobs()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[4 * 1024];
            var stride = UnsafeUtility.SizeOf<ExampleStruct>();

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct {someData = 0};

            Setup(initialData);

            var job = new UploadJob();
            var totalSize = initialData.Length * stride;
            job.uploader = uploader.Begin(totalSize, stride, initialData.Length);
            job.Schedule(initialData.Length, 64).Complete();
            uploader.EndAndCommit(job.uploader);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(i, resultingData[i].someData);

            Teardown();
        }

        static void CompareFloats(float expected, float actual, float delta = 0.00001f)
        {
            Assert.LessOrEqual(math.abs(expected - actual), delta);
        }

        static void CompareMatrices(float4x4 expected, float4x4 actual, float delta = 0.00001f)
        {
            for (int i = 0; i < 4; ++i)
            {
                for (int j = 0; j < 4; ++j)
                {
                    CompareFloats(expected[i][j], actual[i][j], delta);
                }
            }
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public unsafe void MatrixUploads4x4()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var numMatrices = 1025;
            var initialData = new float4x4[numMatrices];

            for (int i = 0; i < numMatrices; ++i)
                initialData[i] = float4x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;
            var tsu = uploader.Begin(totalSize, totalSize, 1);
            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, numMatrices * 64,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType4x4);
            uploader.EndAndCommit(tsu);
            deltaData.Dispose();

            var resultingData = new float4x4[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < numMatrices; ++i)
            {
                var mat = GenerateTestMatrix(i);

                CompareMatrices(mat, resultingData[i]);
            }

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public unsafe void MatrixUploads4x4To3x4()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var numMatrices = 1025;
            var initialData = new float3x4[numMatrices];

            for (int i = 0; i < numMatrices; ++i)
                initialData[i] = float3x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;
            var tsu = uploader.Begin(totalSize, totalSize, 1);
            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, numMatrices * 64,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType3x4);
            uploader.EndAndCommit(tsu);
            deltaData.Dispose();

            var resultingData = new float3x4[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < numMatrices; ++i)
            {
                var mat = GenerateTestMatrix(i);

                var actual = resultingData[i];
                var actual4x4 = ExpandMatrix(actual);

                CompareMatrices(mat, actual4x4);
            }

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public unsafe void InverseMatrixUploads4x4()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var numMatrices = 1025;
            var initialData = new float4x4[numMatrices * 2];

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = float4x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;
            var tsu = uploader.Begin(totalSize, totalSize, 1);
            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, numMatrices * 64,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType4x4);
            uploader.EndAndCommit(tsu);

            deltaData.Dispose();

            var resultingData = new float4x4[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < numMatrices; ++i)
            {
                var mat = GenerateTestMatrix(i);
                var matInv = math.fastinverse(mat);

                CompareMatrices(mat, resultingData[numMatrices * 0 + i]);
                CompareMatrices(matInv, resultingData[numMatrices * 1 + i], 0.001f); // Inverse matrices might differ more between CPU and GPU
            }

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public unsafe void InverseMatrixUploads4x4To3x4()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var numMatrices = 1025;
            var initialData = new float3x4[numMatrices * 2];

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = float3x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;
            var tsu = uploader.Begin(totalSize, totalSize, 1);
            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, numMatrices * 48,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType3x4);
            uploader.EndAndCommit(tsu);

            deltaData.Dispose();

            var resultingData = new float3x4[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < numMatrices; ++i)
            {
                var mat = GenerateTestMatrix(i);
                var matInv = math.fastinverse(mat);

                var actual4x4 = ExpandMatrix(resultingData[i]);
                var actual4x4Inv = ExpandMatrix(resultingData[numMatrices + i]);

                CompareMatrices(mat, actual4x4);
                CompareMatrices(matInv, actual4x4Inv, 0.001f); // Inverse matrices might differ more between CPU and GPU
            }

            Teardown();
        }

#if UNITY_2020_1_OR_NEWER
        [Test]
#endif
        public void HugeUploadCount()
        {
            const int HugeCount = 100000;

            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[HugeCount];

            Setup(initialData);
            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var tsu = uploader.Begin(structSize * HugeCount, structSize, HugeCount);
            for (int i = 0; i < initialData.Length; ++i)
                tsu.AddUpload(new ExampleStruct {someData = i}, 4 * i);
            uploader.EndAndCommit(tsu);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < initialData.Length; ++i)
                Assert.AreEqual(i, resultingData[i].someData, $"Index: {i}");

            Teardown();
        }
    }
}
