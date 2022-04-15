using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation
{
    class EmitterContext
    {
        public DecodedProgram Program { get; }
        public ShaderConfig Config { get; }

        public bool IsNonMain { get; }

        public Block CurrBlock { get; set; }
        public InstOp CurrOp { get; set; }

        public int OperationsCount => _operations.Count;

        private readonly List<Operation> _operations;
        private readonly Dictionary<ulong, Operand> _labels;

        public EmitterContext(DecodedProgram program, ShaderConfig config, bool isNonMain)
        {
            Program = program;
            Config = config;
            IsNonMain = isNonMain;
            _operations = new List<Operation>();
            _labels = new Dictionary<ulong, Operand>();

            EmitStart();
        }

        private void EmitStart()
        {
            if (Config.Stage == ShaderStage.Vertex &&
                Config.Options.TargetApi == TargetApi.Vulkan &&
                (Config.Options.Flags & TranslationFlags.VertexA) == 0)
            {
                // Vulkan requires the point size to be always written on the shader if the primitive topology is points.
                this.Copy(Attribute(AttributeConsts.PointSize), ConstF(Config.GpuAccessor.QueryPointSize()));
            }
        }

        public T GetOp<T>() where T : unmanaged
        {
            Debug.Assert(Unsafe.SizeOf<T>() == sizeof(ulong));
            ulong op = CurrOp.RawOpCode;
            return Unsafe.As<ulong, T>(ref op);
        }

        public Operand Add(Instruction inst, Operand dest = null, params Operand[] sources)
        {
            Operation operation = new Operation(inst, dest, sources);

            _operations.Add(operation);

            return dest;
        }

        public (Operand, Operand) Add(Instruction inst, (Operand, Operand) dest, params Operand[] sources)
        {
            Operand[] dests = new[] { dest.Item1, dest.Item2 };

            Operation operation = new Operation(inst, 0, dests, sources);

            Add(operation);

            return dest;
        }

        public void Add(Operation operation)
        {
            _operations.Add(operation);
        }

        public TextureOperation CreateTextureOperation(
            Instruction inst,
            SamplerType type,
            TextureFlags flags,
            int handle,
            int compIndex,
            Operand dest,
            params Operand[] sources)
        {
            return CreateTextureOperation(inst, type, TextureFormat.Unknown, flags, handle, compIndex, dest, sources);
        }

        public TextureOperation CreateTextureOperation(
            Instruction inst,
            SamplerType type,
            TextureFormat format,
            TextureFlags flags,
            int handle,
            int compIndex,
            Operand dest,
            params Operand[] sources)
        {
            if (!flags.HasFlag(TextureFlags.Bindless))
            {
                Config.SetUsedTexture(inst, type, format, flags, TextureOperation.DefaultCbufSlot, handle);
            }

            return new TextureOperation(inst, type, format, flags, handle, compIndex, dest, sources);
        }

        public void FlagAttributeRead(int attribute)
        {
            if (Config.Stage == ShaderStage.Vertex && attribute == AttributeConsts.InstanceId)
            {
                Config.SetUsedFeature(FeatureFlags.InstanceId);
            }
            else if (Config.Stage == ShaderStage.Fragment)
            {
                switch (attribute)
                {
                    case AttributeConsts.PositionX:
                    case AttributeConsts.PositionY:
                        Config.SetUsedFeature(FeatureFlags.FragCoordXY);
                        break;
                }
            }
        }

        public void FlagAttributeWritten(int attribute)
        {
            if (Config.Stage == ShaderStage.Vertex)
            {
                switch (attribute)
                {
                    case AttributeConsts.ClipDistance0:
                    case AttributeConsts.ClipDistance1:
                    case AttributeConsts.ClipDistance2:
                    case AttributeConsts.ClipDistance3:
                    case AttributeConsts.ClipDistance4:
                    case AttributeConsts.ClipDistance5:
                    case AttributeConsts.ClipDistance6:
                    case AttributeConsts.ClipDistance7:
                        Config.SetClipDistanceWritten((attribute - AttributeConsts.ClipDistance0) / 4);
                        break;
                }
            }

            if (Config.Stage != ShaderStage.Fragment && attribute == AttributeConsts.Layer)
            {
                Config.SetUsedFeature(FeatureFlags.RtLayer);
            }
        }

        public void MarkLabel(Operand label)
        {
            Add(Instruction.MarkLabel, label);
        }

        public Operand GetLabel(ulong address)
        {
            if (!_labels.TryGetValue(address, out Operand label))
            {
                label = Label();

                _labels.Add(address, label);
            }

            return label;
        }

        public void PrepareForReturn()
        {
            if (IsNonMain)
            {
                return;
            }

            if (Config.Options.TargetApi == TargetApi.Vulkan &&
                Config.Stage == ShaderStage.Vertex &&
                (Config.Options.Flags & TranslationFlags.VertexA) == 0)
            {
                if (Config.GpuAccessor.QueryTransformDepthMinusOneToOne())
                {
                    Operand z = Attribute(AttributeConsts.PositionZ | AttributeConsts.LoadOutputMask);
                    Operand w = Attribute(AttributeConsts.PositionW | AttributeConsts.LoadOutputMask);
                    Operand halfW = this.FPMultiply(w, ConstF(0.5f));

                    this.Copy(Attribute(AttributeConsts.PositionZ), this.FPFusedMultiplyAdd(z, ConstF(0.5f), halfW));
                }
            }
            else if (Config.Stage == ShaderStage.Geometry)
            {
                void WriteOutput(int index, int primIndex)
                {
                    Operand x = this.LoadAttribute(Const(index), Const(0), Const(primIndex));
                    Operand y = this.LoadAttribute(Const(index + 4), Const(0), Const(primIndex));
                    Operand z = this.LoadAttribute(Const(index + 8), Const(0), Const(primIndex));
                    Operand w = this.LoadAttribute(Const(index + 12), Const(0), Const(primIndex));

                    this.Copy(Attribute(index), x);
                    this.Copy(Attribute(index + 4), y);
                    this.Copy(Attribute(index + 8), z);
                    this.Copy(Attribute(index + 12), w);
                }

                if (Config.GpPassthrough)
                {
                    int inputVertices = Config.GpuAccessor.QueryPrimitiveTopology().ToInputVertices();

                    for (int primIndex = 0; primIndex < inputVertices; primIndex++)
                    {
                        WriteOutput(AttributeConsts.PositionX, primIndex);

                        int passthroughAttributes = Config.PassthroughAttributes;
                        while (passthroughAttributes != 0)
                        {
                            int index = BitOperations.TrailingZeroCount(passthroughAttributes);
                            WriteOutput(AttributeConsts.UserAttributeBase + index * 16, primIndex);
                            Config.SetOutputUserAttribute(index, perPatch: false);
                            passthroughAttributes &= ~(1 << index);
                        }

                        this.EmitVertex();
                    }

                    this.EndPrimitive();
                }
            }
            else if (Config.Stage == ShaderStage.Fragment)
            {
                bool supportsBgra = Config.GpuAccessor.QueryHostSupportsBgraFormat();

                if (Config.OmapDepth)
                {
                    Operand dest = Attribute(AttributeConsts.FragmentOutputDepth);

                    Operand src = Register(Config.GetDepthRegister(), RegisterType.Gpr);

                    this.Copy(dest, src);
                }

                AlphaTestOp alphaTestOp = Config.GpuAccessor.QueryAlphaTestCompare();

                if (alphaTestOp != AlphaTestOp.Always && (Config.OmapTargets & 8) != 0)
                {
                    if (alphaTestOp == AlphaTestOp.Never)
                    {
                        this.Discard();
                    }
                    else
                    {
                        Instruction comparator = alphaTestOp switch
                        {
                            AlphaTestOp.Equal => Instruction.CompareEqual,
                            AlphaTestOp.Greater => Instruction.CompareGreater,
                            AlphaTestOp.GreaterOrEqual => Instruction.CompareGreaterOrEqual,
                            AlphaTestOp.Less => Instruction.CompareLess,
                            AlphaTestOp.LessOrEqual => Instruction.CompareLessOrEqual,
                            AlphaTestOp.NotEqual => Instruction.CompareNotEqual,
                            _ => 0
                        };

                        Debug.Assert(comparator != 0, $"Invalid alpha test operation \"{alphaTestOp}\".");

                        Operand alpha = Register(3, RegisterType.Gpr);
                        Operand alphaRef = ConstF(Config.GpuAccessor.QueryAlphaTestReference());
                        Operand alphaPass = Add(Instruction.FP32 | comparator, Local(), alpha, alphaRef);
                        Operand alphaPassLabel = Label();

                        this.BranchIfTrue(alphaPassLabel, alphaPass);
                        this.Discard();
                        this.MarkLabel(alphaPassLabel);
                    }
                }

                int regIndexBase = 0;

                for (int rtIndex = 0; rtIndex < 8; rtIndex++)
                {
                    for (int component = 0; component < 4; component++)
                    {
                        bool componentEnabled = (Config.OmapTargets & (1 << (rtIndex * 4 + component))) != 0;
                        if (!componentEnabled)
                        {
                            continue;
                        }

                        int fragmentOutputColorAttr = AttributeConsts.FragmentOutputColorBase + rtIndex * 16;

                        Operand src = Register(regIndexBase + component, RegisterType.Gpr);

                        // Perform B <-> R swap if needed, for BGRA formats (not supported on OpenGL).
                        if (!supportsBgra && (component == 0 || component == 2))
                        {
                            Operand isBgra = Attribute(AttributeConsts.FragmentOutputIsBgraBase + rtIndex * 4);

                            Operand lblIsBgra = Label();
                            Operand lblEnd = Label();

                            this.BranchIfTrue(lblIsBgra, isBgra);

                            this.Copy(Attribute(fragmentOutputColorAttr + component * 4), src);
                            this.Branch(lblEnd);

                            MarkLabel(lblIsBgra);

                            this.Copy(Attribute(fragmentOutputColorAttr + (2 - component) * 4), src);

                            MarkLabel(lblEnd);
                        }
                        else
                        {
                            this.Copy(Attribute(fragmentOutputColorAttr + component * 4), src);
                        }
                    }

                    bool targetEnabled = (Config.OmapTargets & (0xf << (rtIndex * 4))) != 0;
                    if (targetEnabled)
                    {
                        Config.SetOutputUserAttribute(rtIndex, perPatch: false);
                        regIndexBase += 4;
                    }
                }
            }
        }

        public Operation[] GetOperations()
        {
            return _operations.ToArray();
        }
    }
}