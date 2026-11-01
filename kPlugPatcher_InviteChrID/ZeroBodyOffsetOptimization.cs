using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    // BonesCtrl の加算補正がゼロのときだけ読み書きを省く。計時・統計・骨参照の置換は行わない。
    internal static class ZeroBodyOffsetOptimization
    {
        private const string Tag="[ZeroBodyOffsets]";
        private static readonly string Owner=PatcherPlugin.GUID+".zerobodyoffsets";
        private static Harmony _harmony;
        private static Type _core,_bones;
        private static MethodInfo _parent;
        private static FieldInfo _hctrl,_mainBone;
        private static bool _applied,_blocked,_conflictReported,_lastSetting;
        private static float _nextPoll,_seenAt;
        private static int _seenFrame,_seenId;
        internal static string AssemblyForProcess(string process){return process.EndsWith("VR",StringComparison.OrdinalIgnoreCase)?"kPlugVR":"kPlug";}
        private static string AssemblyName { get { return AssemblyForProcess(Paths.ProcessName); } }
        private static string Journal { get { return Path.Combine(Paths.ConfigPath,AssemblyName=="kPlugVR"?"kkttspj.kplugpatcher.zero_body_offsets_vr_patching.txt":"kkttspj.kplugpatcher.zero_body_offsets_patching.txt"); } }
        private static string Skip { get { return Path.Combine(Paths.ConfigPath,AssemblyName=="kPlugVR"?"kkttspj.kplugpatcher.zero_body_offsets_vr_skip.txt":"kkttspj.kplugpatcher.zero_body_offsets_skip.txt"); } }
        public static bool IsEnabled(){return _applied&&!PatcherPlugin.IsQuitting&&PatcherPlugin.SkipZeroBodyOffsets!=null&&PatcherPlugin.SkipZeroBodyOffsets.Value;}
        public static bool ExactZero(Vector3 v){return v.x==0f&&v.y==0f&&v.z==0f;}
        public static bool ShouldSkip(Transform target,Vector3 delta){return ExactZero(delta)&&target!=null;}

        internal static void Tick()
        {
            bool setting=PatcherPlugin.SkipZeroBodyOffsets!=null&&PatcherPlugin.SkipZeroBodyOffsets.Value;
            if(setting!=_lastSetting){_lastSetting=setting;PatcherPlugin.Logger.LogInfo(Tag+" 設定="+setting+(_applied?"（false時は元の命令を実行）":""));}
            if(!setting||_applied||_blocked||Time.realtimeSinceStartup<_nextPoll)return;
            _nextPoll=Time.realtimeSinceStartup+1f;
            bool wroteJournal=false;
            try
            {
                if(_parent==null)
                {
                    string wanted=AssemblyName;
                    var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==wanted);
                    if(assembly==null)return;
                    _core=assembly.GetType(wanted+".Core");_bones=assembly.GetType(wanted+".CmpChara.BonesCtrl");
                    _parent=_bones==null?null:AccessTools.DeclaredMethod(_bones,"LateUpdate",Type.EmptyTypes);
                    _hctrl=_core==null?null:AccessTools.Field(_core,"hCtrl");
                    if(_parent==null||_hctrl==null)throw new InvalidOperationException("対象の型/メソッド/既存参照がありません");
                    _mainBone=AccessTools.Field(_hctrl.FieldType,"bcG");
                    if(_mainBone==null)throw new InvalidOperationException("HCtrl.bcGがありません");
                    if(File.Exists(Journal)){File.AppendAllText(Skip,File.ReadAllText(Journal).Trim()+Environment.NewLine);File.Delete(Journal);}
                    if(File.Exists(Skip)&&File.ReadAllLines(Skip).Contains(_parent.DeclaringType.FullName+".LateUpdate"))throw new InvalidOperationException("前回適用時の異常記録があるため除外しました: "+Skip);
                }
                var ctrl=_hctrl.GetValue(null) as MonoBehaviour;
                var bone=ctrl==null?null:_mainBone.GetValue(ctrl) as MonoBehaviour;
                if(bone==null||!bone.isActiveAndEnabled){_seenId=0;return;}
                if(_seenId!=bone.GetInstanceID()){_seenId=bone.GetInstanceID();_seenAt=Time.realtimeSinceStartup;_seenFrame=Time.frameCount;return;}
                // 未JITのUnityコールバックへ早期に当てない。既存の活動中部品を2秒以上確認する。
                if(Time.realtimeSinceStartup-_seenAt<2f||Time.frameCount-_seenFrame<2)return;
                var patches=Harmony.GetPatchInfo(_parent);
                if(patches!=null&&patches.Transpilers.Any(p=>p.owner!=Owner))
                {
                    if(!_conflictReported){_conflictReported=true;PatcherPlugin.Logger.LogWarning(Tag+" 他のTranspilerと競合しています。BonesCtrl を書き換える他のプラグインをオフにして待ってください。他の本体変更パッチとの併用も保留します。");}
                    return;
                }
                File.WriteAllText(Journal,_parent.DeclaringType.FullName+".LateUpdate");
                wroteJournal=true;
                _harmony=new Harmony(Owner);
                _harmony.Patch(_parent,transpiler:new HarmonyMethod(typeof(ZeroBodyOffsetOptimization),nameof(Transpiler)));
                _applied=true;
                PatcherPlugin.Logger.LogInfo(Tag+" 適用しました: "+_parent.DeclaringType.FullName+".LateUpdate / 加算10か所。ゲーム中にSkipZeroBodyOffsets=falseで無効化できます。");
            }
            catch(Exception ex)
            {
                _blocked=true;_applied=false;
                try{if(_harmony!=null&&_parent!=null)_harmony.Unpatch(_parent,HarmonyPatchType.Transpiler,Owner);}catch(Exception cleanup){PatcherPlugin.Logger.LogWarning(Tag+" 解除失敗（ガードは無効のまま）: "+cleanup.Message);}
                PatcherPlugin.Logger.LogWarning(Tag+" 適用を中止しました（再起動まで再試行しません）: "+ex.Message);
            }
            finally{try{if(wroteJournal&&File.Exists(Journal))File.Delete(Journal);}catch{}}
        }

        private static string Shape(List<CodeInstruction> code)
        {
            var labels=new Dictionary<Label,int>();for(int i=0;i<code.Count;i++)foreach(var l in code[i].labels)labels[l]=i;
            return string.Join("\n",code.Select(c=>{
                object op=c.operand;string value="";
                if(op is Label)value="target:"+labels[(Label)op];
                else if(op is Label[])value=string.Join(",",((Label[])op).Select(l=>labels[l].ToString()).ToArray());
                else if(op is LocalVariableInfo){var local=(LocalVariableInfo)op;value="local:"+local.LocalIndex+":"+local.LocalType.AssemblyQualifiedName;}
                else if(op is Type)value=((Type)op).AssemblyQualifiedName;
                else if(op is MemberInfo){var member=(MemberInfo)op;value=(member.DeclaringType==null?"global":member.DeclaringType.AssemblyQualifiedName)+":"+member;}
                else if(op!=null)value=Convert.ToString(op,CultureInfo.InvariantCulture);
                return c.opcode.Value+":"+value+":"+string.Join(",",c.blocks.Select(b=>b.blockType+":"+b.catchType).ToArray());
            }).ToArray());
        }
        private static CodeInstruction Call(string name){return new CodeInstruction(OpCodes.Call,typeof(ZeroBodyOffsetOptimization).GetMethod(name));}
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,ILGenerator generator,MethodBase __originalMethod)
        {
            return Rewrite(instructions,generator,(MethodInfo)__originalMethod);
        }
        internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,ILGenerator generator,MethodInfo parent)
        {
            var il=instructions.Select(c=>new CodeInstruction(c)).ToList();
            if(parent.IsStatic||parent.ReturnType!=typeof(void)||parent.GetParameters().Length!=0||Shape(il)!=Shape(PatchProcessor.GetOriginalInstructions(parent)))throw new InvalidOperationException("元IL/シグネチャと一致しません");
            if(parent.GetMethodBody().LocalVariables.Count<2||parent.GetMethodBody().LocalVariables[1].LocalType!=typeof(Vector3))throw new InvalidOperationException("補正用ローカルが不一致");
            string[] fields={"root","root","hipShaper_L","hipShaper_R","waist","spine1","spine2","spine3","neck","head"};
            var ranges=new List<int[]>();
            for(int i=0;i+4<il.Count&&ranges.Count<10;i++)
            {
                var f=il[i+1].operand as FieldInfo;var get=il[i+3].operand as MethodInfo;
                if(il[i].opcode!=OpCodes.Ldarg_0||il[i+1].opcode!=OpCodes.Ldfld||f==null||f.DeclaringType!=parent.DeclaringType||f.FieldType!=typeof(Transform)||il[i+2].opcode!=OpCodes.Dup||get==null||get.DeclaringType!=typeof(Transform)||(get.Name!="get_localPosition"&&get.Name!="get_localEulerAngles"))continue;
                int site=ranges.Count;string property=site==0||site==2||site==3?"get_localPosition":"get_localEulerAngles";
                if(f.Name!=fields[site]||get.Name!=property||il[i+3].opcode!=OpCodes.Callvirt)throw new InvalidOperationException("加算対象/順序が不一致");
                int end=i+4;while(end<il.Count&&!((il[end].operand as MethodInfo)?.DeclaringType==typeof(Transform)&&((MethodInfo)il[end].operand).Name==get.Name.Replace("get_","set_")))end++;
                if(end>=il.Count||end-i>32||il[end].opcode!=OpCodes.Callvirt||(il[end-1].operand as MethodInfo)?.Name!="op_Addition")throw new InvalidOperationException("加算境界が不一致");
                var stack=new List<Type>();
                for(int j=i;j<=end;j++)
                {
                    if(il[j].blocks.Count!=0||(j>i&&il[j].labels.Count!=0))throw new InvalidOperationException("加算境界に分岐/例外領域");
                    if(j<i+4||j>=end-1)continue;
                    var op=il[j].opcode;var method=il[j].operand as MethodInfo;var field=il[j].operand as FieldInfo;
                    if(op==OpCodes.Ldarg_0)stack.Add(parent.DeclaringType);
                    else if(op==OpCodes.Ldloc_1)stack.Add(typeof(Vector3));
                    else if(op==OpCodes.Ldfld&&field!=null&&field.DeclaringType==parent.DeclaringType&&(field.FieldType==typeof(Vector3)||field.FieldType==typeof(float)))
                    {if(stack.Count==0||stack[stack.Count-1]!=parent.DeclaringType)throw new InvalidOperationException("フィールドのスタック不一致");stack.RemoveAt(stack.Count-1);stack.Add(field.FieldType);}
                    else if(op==OpCodes.Call&&method!=null&&method.DeclaringType==typeof(Vector3)&&new[]{"op_Addition","op_Multiply","get_up"}.Contains(method.Name))
                    {var args=method.GetParameters();for(int n=args.Length-1;n>=0;n--){if(stack.Count==0||stack[stack.Count-1]!=args[n].ParameterType)throw new InvalidOperationException("式のスタック不一致");stack.RemoveAt(stack.Count-1);}stack.Add(method.ReturnType);}
                    else throw new InvalidOperationException("補正式に未知の命令: "+il[j]);
                }
                if(stack.Count!=1||stack[0]!=typeof(Vector3))throw new InvalidOperationException("最終補正がVector3ではありません");
                ranges.Add(new[]{i,end});i=end;
            }
            if(ranges.Count!=10)throw new InvalidOperationException("加算が10か所ではありません");
            var before=new Dictionary<int,List<CodeInstruction>>();
            foreach(var range in ranges)
            {
                int start=range[0],end=range[1];var run=generator.DefineLabel();var skip=generator.DefineLabel();il[end+1].labels.Add(skip);
                var check=new List<CodeInstruction>{Call(nameof(IsEnabled)),new CodeInstruction(OpCodes.Brfalse,run),new CodeInstruction(OpCodes.Ldarg_0),new CodeInstruction(il[start+1])};
                for(int j=start+4;j<end-1;j++)check.Add(new CodeInstruction(il[j]));
                check.Add(Call(nameof(ShouldSkip)));check.Add(new CodeInstruction(OpCodes.Brtrue,skip));
                var nop=new CodeInstruction(OpCodes.Nop);nop.labels.Add(run);check.Add(nop);before[start]=check;
            }
            var output=new List<CodeInstruction>();for(int i=0;i<il.Count;i++){List<CodeInstruction> prefix;if(before.TryGetValue(i,out prefix)){prefix[0].labels.InsertRange(0,il[i].labels);il[i].labels.Clear();output.AddRange(prefix);}output.Add(il[i]);}return output;
        }
    }
}
