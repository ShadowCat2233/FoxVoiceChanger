'use client';

import { useEffect, useState } from 'react';
import {
  Activity, AudioLines, Blocks, Bot, Boxes, ChevronDown, CircleGauge, Cpu,
  Download, Gamepad2, Headphones, Library, Mic2, Music2, Play, Plus, Power,
  Radio, Settings2, ShieldCheck, SlidersHorizontal, Sparkles, Square,
  WandSparkles, Zap,
} from 'lucide-react';

import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Progress } from '@/components/ui/progress';
import { Slider } from '@/components/ui/slider';
import {
  Sidebar, SidebarContent, SidebarFooter, SidebarGroup, SidebarGroupContent,
  SidebarGroupLabel, SidebarHeader, SidebarInset, SidebarMenu,
  SidebarMenuButton, SidebarMenuItem, SidebarProvider, SidebarTrigger,
} from '@/components/ui/sidebar';
import { Switch } from '@/components/ui/switch';

type View = 'studio' | 'models' | 'soundboard' | 'training' | 'components';

type WebMcpContext = {
  registerTool: (
    tool: {
      name: string;
      title: string;
      description: string;
      inputSchema: Record<string, unknown>;
      annotations: { readOnlyHint: boolean; untrustedContentHint: boolean };
      execute: (input: unknown) => unknown;
    },
    options?: { signal?: AbortSignal },
  ) => void | Promise<void>;
};

const navItems = [
  { id: 'studio' as const, label: '实时变声', icon: AudioLines },
  { id: 'models' as const, label: '模型库', icon: Library },
  { id: 'soundboard' as const, label: '音效板', icon: Music2 },
  { id: 'training' as const, label: '模型训练', icon: Bot },
  { id: 'components' as const, label: '组件中心', icon: Boxes },
];

const waveform = [22, 34, 28, 46, 70, 52, 86, 60, 42, 76, 94, 56, 32, 62, 82, 48, 72, 40, 28, 52, 36, 22];

const models = [
  { name: '霜尾', alias: 'Frost Tail', tone: '清亮 · 自然', accent: 'cyan', active: true },
  { name: '绯夜', alias: 'Crimson Night', tone: '低沉 · 电影感', accent: 'pink', active: false },
  { name: '星港', alias: 'Star Harbor', tone: '中性 · 广播感', accent: 'violet', active: false },
];

function BrandMark() {
  return <div className="brand-mark" aria-hidden="true"><span /><span /></div>;
}

function SectionTitle({ eyebrow, title, action }: { eyebrow: string; title: string; action?: React.ReactNode }) {
  return <div className="section-heading"><div><p>{eyebrow}</p><h2>{title}</h2></div>{action}</div>;
}

function VoiceOrb({ running }: { running: boolean }) {
  return (
    <div className={`voice-orb ${running ? 'is-running' : ''}`}>
      <div className="orb-ring orb-ring-one" /><div className="orb-ring orb-ring-two" />
      <div className="orb-core">
        <div className="waveform" aria-label={running ? '正在接收麦克风输入' : '变声已停止'}>
          {waveform.map((height, index) => <span key={`${height}-${index}`} style={{ height: `${running ? height : Math.min(height, 22)}%` }} />)}
        </div>
      </div>
    </div>
  );
}

function StudioView({ running, setRunning }: { running: boolean; setRunning: (value: boolean) => void }) {
  return (
    <div className="workspace-grid">
      <section className="studio-panel panel panel-glow">
        <SectionTitle eyebrow="LIVE VOICE" title="实时变声" action={<Badge className="status-badge"><span className="status-dot" />DirectML 已就绪</Badge>} />
        <div className="studio-stage">
          <div className="device-route">
            <div><Mic2 /><span><small>输入</small>麦克风阵列</span></div><i />
            <div><Radio /><span><small>输出</small>VB-CABLE Input</span></div>
          </div>
          <VoiceOrb running={running} />
          <div className="active-model">
            <span className="model-avatar cyan"><Sparkles /></span>
            <div><small>当前模型</small><strong>霜尾 <em>Frost Tail</em></strong></div>
            <Button variant="ghost" size="icon-sm" aria-label="选择模型"><ChevronDown /></Button>
          </div>
          <Button size="lg" className={`power-button ${running ? 'stop' : ''}`} onClick={() => setRunning(!running)}>
            {running ? <Square fill="currentColor" /> : <Play fill="currentColor" />}{running ? '停止变声' : '开始变声'}
          </Button>
          <p className="shortcut">全局快捷键 <kbd>Ctrl</kbd><span>+</span><kbd>F8</kbd></p>
        </div>
      </section>

      <aside className="right-column">
        <section className="panel compact-panel">
          <SectionTitle eyebrow="GAME GUARD" title="游戏保护" action={<Switch defaultChecked aria-label="启用游戏保护" />} />
          <div className="guard-state"><span><ShieldCheck /></span><div><strong>资源保护已启用</strong><small>游戏满载时自动保留语音预算</small></div></div>
          <div className="metric-row"><span>推理预算</span><strong>13.4 <small>/ 20 ms</small></strong></div>
          <Progress value={67} className="budget-progress" />
          <div className="guard-tags"><span>高优先级音频</span><span>动画限帧</span><span>自动旁路</span></div>
        </section>
        <section className="panel compact-panel">
          <SectionTitle eyebrow="QUICK TUNE" title="快速调音" action={<SlidersHorizontal className="section-icon" />} />
          <div className="slider-control"><div><label>音高</label><output>+4.0</output></div><Slider defaultValue={[62]} min={0} max={100} aria-label="音高" /><div className="scale"><span>-12</span><span>0</span><span>+12</span></div></div>
          <div className="slider-control"><div><label>音色融合</label><output>72%</output></div><Slider defaultValue={[72]} aria-label="音色融合" /></div>
          <div className="tune-switch"><span><strong>轻量降噪</strong><small>RNNoise · 低开销</small></span><Switch defaultChecked aria-label="轻量降噪" /></div>
          <div className="tune-switch"><span><strong>监听自己的声音</strong><small>默认关闭以防啸叫</small></span><Switch aria-label="监听自己的声音" /></div>
        </section>
      </aside>

      <section className="panel presets-panel">
        <SectionTitle eyebrow="VOICE PRESETS" title="声音预设" action={<Button variant="ghost" size="sm">查看全部</Button>} />
        <div className="preset-grid">
          {models.map((model) => (
            <button key={model.name} className={`preset-card ${model.active ? 'active' : ''}`}>
              <span className={`model-avatar ${model.accent}`}><WandSparkles /></span>
              <span><strong>{model.name}</strong><small>{model.alias}</small></span><em>{model.tone}</em>
              {model.active && <Badge>使用中</Badge>}
            </button>
          ))}
          <button className="preset-card add-card"><Plus /><span><strong>添加模型</strong><small>本地或 Hugging Face</small></span></button>
        </div>
      </section>
    </div>
  );
}

function ModelsView() {
  return (
    <section className="panel full-panel">
      <SectionTitle eyebrow="MODEL LIBRARY" title="模型库" action={<Button><Plus />导入模型</Button>} />
      <div className="info-banner"><ShieldCheck /><span><strong>所有模型都保存在本机</strong><small>导入时会检查格式、哈希、许可证与 ONNX 输入输出结构。</small></span></div>
      <div className="model-table">
        {models.map((model, index) => (
          <article key={model.name}>
            <span className={`model-avatar ${model.accent}`}><Sparkles /></span>
            <div className="model-name"><strong>{model.name}</strong><small>{model.alias} · RVC v2 · 40 kHz</small></div>
            <div className="model-meta"><small>推理格式</small><strong>ONNX FP16</strong></div>
            <div className="model-meta"><small>检索索引</small><strong>{index === 1 ? '未提供' : '已保存 · 未启用'}</strong></div>
            <Badge variant={index === 0 ? 'default' : 'outline'}>{index === 0 ? '当前使用' : '可用'}</Badge>
            <Button variant="ghost" size="icon-sm" aria-label={`${model.name}模型设置`}><Settings2 /></Button>
          </article>
        ))}
      </div>
    </section>
  );
}

function ComponentsView() {
  const components = [
    { icon: Cpu, name: 'WindowsML 引擎', desc: 'DirectML 通用推理与 CPU 回退', tag: '默认', state: '已安装', value: 100 },
    { icon: Zap, name: 'TensorRT 引擎', desc: 'NVIDIA 高性能推理组件', tag: '可选', state: '未安装', value: 0 },
    { icon: Radio, name: 'VB-CABLE', desc: '将变声结果送入游戏和语音软件', tag: '音频', state: '已连接', value: 100 },
    { icon: Blocks, name: '模型转换器', desc: '在隔离进程中转换 RVC v2 模型', tag: '工具', state: '可更新', value: 82 },
  ];
  return (
    <section className="panel full-panel">
      <SectionTitle eyebrow="COMPONENT CENTER" title="组件中心" action={<Button variant="outline"><Activity />重新检测</Button>} />
      <div className="hardware-card"><span><CircleGauge /></span><div><small>当前推荐方案</small><strong>WindowsML · DirectML</strong><p>兼容当前图形适配器，优先保证游戏中的稳定性。</p></div><Badge className="status-badge"><span className="status-dot" />自检通过</Badge></div>
      <div className="component-list">
        {components.map((item) => (
          <article key={item.name}>
            <span className="component-icon"><item.icon /></span>
            <div className="component-copy"><div><strong>{item.name}</strong><Badge variant="outline">{item.tag}</Badge></div><small>{item.desc}</small></div>
            <div className="component-status"><strong>{item.state}</strong><Progress value={item.value} /></div>
            <Button variant={item.value === 0 ? 'default' : 'outline'}>{item.value === 0 && <Download />}{item.value === 0 ? '安装' : '管理'}</Button>
          </article>
        ))}
      </div>
    </section>
  );
}

function PlaceholderView({ view }: { view: View }) {
  const data = {
    soundboard: { icon: Music2, title: '音效板', body: '创建音效分组、设置全局热键，并通过独立限制器安全混入语音输出。' },
    training: { icon: Bot, title: '模型训练', body: '训练环境将在硬件自检通过后按需安装。游戏运行时训练任务会自动暂停。' },
    studio: { icon: AudioLines, title: '实时变声', body: '' }, models: { icon: Library, title: '模型库', body: '' }, components: { icon: Boxes, title: '组件中心', body: '' },
  }[view];
  return <section className="panel full-panel empty-view"><span><data.icon /></span><p>COMING NEXT</p><h2>{data.title}</h2><p>{data.body}</p><Button variant="outline">查看实施计划</Button></section>;
}

export default function Home() {
  const [view, setView] = useState<View>('studio');
  const [running, setRunning] = useState(false);
  const title = navItems.find((item) => item.id === view)?.label;

  useEffect(() => {
    const context = (document as Document & { modelContext?: WebMcpContext }).modelContext;
    if (!context?.registerTool) return;
    const lifecycle = new AbortController();
    void Promise.resolve(context.registerTool({
      name: 'set_voice_conversion',
      title: '设置实时变声状态',
      description: '启动或停止 FoxVoice 的实时变声，并同步更新工作台状态。',
      inputSchema: {
        type: 'object',
        properties: { enabled: { type: 'boolean' } },
        required: ['enabled'],
        additionalProperties: false,
      },
      annotations: { readOnlyHint: false, untrustedContentHint: false },
      execute(input) {
        if (!input || typeof input !== 'object' || typeof (input as { enabled?: unknown }).enabled !== 'boolean') {
          throw new Error('enabled 必须是布尔值');
        }
        const enabled = (input as { enabled: boolean }).enabled;
        setView('studio');
        setRunning(enabled);
        return { enabled, engine: 'DirectML', mode: enabled ? 'running' : 'standby' };
      },
    }, { signal: lifecycle.signal })).catch(() => undefined);
    return () => lifecycle.abort();
  }, []);

  return (
    <SidebarProvider defaultOpen>
      <Sidebar collapsible="icon" className="fox-sidebar">
        <SidebarHeader className="sidebar-brand"><BrandMark /><div><strong>狐声</strong><small>FOXVOICE</small></div></SidebarHeader>
        <SidebarContent><SidebarGroup><SidebarGroupLabel>工作台</SidebarGroupLabel><SidebarGroupContent><SidebarMenu>
          {navItems.map((item) => <SidebarMenuItem key={item.id}><SidebarMenuButton tooltip={item.label} isActive={view === item.id} onClick={() => setView(item.id)}><item.icon /><span>{item.label}</span></SidebarMenuButton></SidebarMenuItem>)}
        </SidebarMenu></SidebarGroupContent></SidebarGroup></SidebarContent>
        <SidebarFooter><div className="privacy-note"><ShieldCheck /><span><strong>本地模式</strong><small>音频不会上传</small></span></div><SidebarMenu><SidebarMenuItem><SidebarMenuButton tooltip="设置"><Settings2 /><span>设置</span></SidebarMenuButton></SidebarMenuItem></SidebarMenu></SidebarFooter>
      </Sidebar>

      <SidebarInset className="app-shell">
        <header className="topbar"><div className="topbar-title"><SidebarTrigger /><div><p>FOXVOICE / {title}</p><h1>{view === 'studio' ? '让声音保持在游戏里' : title}</h1></div></div><div className="runtime-strip"><span><Cpu /><small>引擎</small><strong>DirectML</strong></span><span><CircleGauge /><small>延迟</small><strong>{running ? '78 ms' : '-- ms'}</strong></span><Button variant="outline" size="sm"><Headphones />设备</Button></div></header>
        <div className="workspace">{view === 'studio' && <StudioView running={running} setRunning={setRunning} />}{view === 'models' && <ModelsView />}{view === 'components' && <ComponentsView />}{(view === 'soundboard' || view === 'training') && <PlaceholderView view={view} />}</div>
        <footer className="statusbar"><span><i className={running ? 'online' : ''} />{running ? '实时引擎运行中' : '引擎待机'}</span><span><Gamepad2 />游戏保护已启用</span><span><Power />本地模式</span><small>v0.1 prototype</small></footer>
      </SidebarInset>
    </SidebarProvider>
  );
}
