using Unity.Jobs;
using Unity.Burst;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using InfinityTech.Core.Geometry;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Landscape.FoliagePipeline
{
#if UNITY_EDITOR
    interface ITask
    {
        void Execute();
    }

    public struct FUpdateTreeTask : ITask
    {
        public int length;
        public float2 scale;
        public TreePrototype treePrototype;
        public TreeInstance[] treeInstances;
        public TreePrototype[] treePrototypes;
        public List<FTransform> treeTransfroms;


        public void Execute()
        {
            FTransform transform = new FTransform();

            for (int i = 0; i < length; ++i)
            {
                ref TreeInstance treeInstance = ref treeInstances[i];
                TreePrototype serchTreePrototype = treePrototypes[treeInstance.prototypeIndex];
                if (serchTreePrototype.Equals(treePrototype))
                {
                    transform.rotation = new float3(0, treeInstance.rotation, 0);
                    transform.position = treeInstance.position * new float3(scale.x, scale.y, scale.x);
                    transform.scale = new float3(treeInstance.widthScale, treeInstance.heightScale, treeInstance.widthScale);
                    treeTransfroms.Add(transform);
                }
            }
        }
    }

    public struct FUpdateTreeJob : IJob
    {
        public GCHandle taskHandle;

        public void Execute()
        {
            ITask task = (ITask)taskHandle.Target;
            task.Execute();
        }
    }
#endif
    [BurstCompile]
    public unsafe struct FTreeBatchLODJob : IJobParallelFor
    {
        [ReadOnly]
        public float3 viewOringin;

        [ReadOnly]
        public float4x4 matrix_Proj;

        [ReadOnly]
        public NativeArray<float> treeBatchLODs;

        [NativeDisableUnsafePtrRestriction]
        public FTreeBatch* treeBatchs;


        public void Execute(int index)
        {
            float screenRadiusSquared = 0;
            ref FTreeBatch treeBatch = ref treeBatchs[index];

            for (int i = treeBatchLODs.Length - 1; i >= 0; --i)
            {
                float LODSize = (treeBatchLODs[i] * treeBatchLODs[i]) * 0.5f;
                //TreeBatch.LODIndex = math.select(TreeBatch.LODIndex, i, LODSize > ScreenRadiusSquared);
                if (screenRadiusSquared < LODSize)
                {
                    treeBatch.lODIndex = i;
                    break;
                }
            }
        }
    }

    [BurstCompile]
    public unsafe struct FTreeBatchCullingJob : IJobParallelFor
    {
        [ReadOnly]
        public int numLOD;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FPlane* planes;

        [ReadOnly]
        public float3 viewOringin;

        [ReadOnly]
        public float4x4 matrix_Proj;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public float* treeLODInfos;

        [NativeDisableUnsafePtrRestriction]
        public FTreeBatch* treeBatchs;

        [WriteOnly]
        public NativeArray<int> viewTreeBatchs;


        public void Execute(int index)
        {
            ref FTreeBatch treeBatch = ref treeBatchs[index];

            //Calculate LOD
            float ScreenRadiusSquared = Geometry.ComputeBoundsScreenRadiusSquared(treeBatch.boundSphere.radius, treeBatch.boundBox.center, viewOringin, matrix_Proj);

            for (int LODIndex = numLOD; LODIndex >= 0; --LODIndex)
            {
                ref float TreeLODInfo = ref treeLODInfos[LODIndex];

                if (mathExtent.sqr(TreeLODInfo * 0.5f) >= ScreenRadiusSquared)
                {
                    treeBatch.lODIndex = LODIndex;
                    break;
                }
            }

            //Culling Batch
            int visible = 1;
            float2 distRadius = new float2(0, 0);

            for (int PlaneIndex = 0; PlaneIndex < 6; ++PlaneIndex)
            {
                ref FPlane plane = ref planes[PlaneIndex];
                distRadius.x = math.dot(plane.normalDist.xyz, treeBatch.boundBox.center) + plane.normalDist.w;
                distRadius.y = math.dot(math.abs(plane.normalDist.xyz), treeBatch.boundBox.extents);

                visible = math.select(visible, 0, distRadius.x + distRadius.y < 0);
            }
            viewTreeBatchs[index] = visible;
        }
    }

    [BurstCompile]
    public unsafe struct FTreeDrawCommandBuildJob : IJob
    {
        public int maxLOD;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FTreeBatch* treeBatchs;

        [ReadOnly]
        public NativeArray<int> viewTreeBatchs;

        [WriteOnly]
        public NativeArray<int> treeBatchIndexs;

        [ReadOnly]
        public NativeList<FTreeElement> treeElements;

        public NativeList<FTreeElement> passTreeElements;

        public NativeList<FTreeDrawCommand> treeDrawCommands;


        public void Execute()
        {
            //Gather PassTreeElement
            FTreeElement treeElement;
            for (int i = 0; i < treeElements.Length; ++i)
            {
                treeElement = treeElements[i];
                ref FTreeBatch TreeBatch = ref treeBatchs[treeElement.batchIndex];

                if (viewTreeBatchs[treeElement.batchIndex] != 0 && treeElement.lODIndex == TreeBatch.lODIndex)
                {
                    passTreeElements.Add(treeElement);
                }
            }

            //Sort PassTreeElement
            //PassTreeElements.Sort();

            //Build TreeDrawCommand
            FTreeElement passTreeElement;
            FTreeElement cachePassTreeElement = new FTreeElement(-1, -1, -1, -1, -1);

            FTreeDrawCommand treeDrawCommand;
            FTreeDrawCommand cacheTreeDrawCommand;

            for (int i = 0; i < passTreeElements.Length; ++i)
            {
                passTreeElement = passTreeElements[i];
                treeBatchIndexs[i] = passTreeElement.batchIndex;

                if (!passTreeElement.Equals(cachePassTreeElement))
                {
                    cachePassTreeElement = passTreeElement;

                    treeDrawCommand.countOffset.x = 0;
                    treeDrawCommand.countOffset.y = i;
                    treeDrawCommand.lODIndex = passTreeElement.lODIndex;
                    treeDrawCommand.matIndex = passTreeElement.matIndex;
                    treeDrawCommand.meshIndex = passTreeElement.meshIndex;
                    //TreeDrawCommand.InstanceGroupID = PassTreeElement.InstanceGroupID;
                    treeDrawCommands.Add(treeDrawCommand);
                }

                cacheTreeDrawCommand = treeDrawCommands[treeDrawCommands.Length - 1];
                cacheTreeDrawCommand.countOffset.x += 1;
                treeDrawCommands[treeDrawCommands.Length - 1] = cacheTreeDrawCommand;
            }
        }
    }
}
