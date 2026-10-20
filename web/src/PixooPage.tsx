import {useEffect,useMemo,useState,type CSSProperties} from 'react'
import {
  Badge,Button,Card,CardAction,CardContent,CardDescription,CardHeader,CardTitle,
  Dialog,DialogContent,DialogDescription,DialogHeader,DialogTitle,EntityCard,Label,
  PanelHeader,ScrollArea,Select,SelectContent,SelectItem,SelectTrigger,SelectValue,Slider,
  Table,TableBody,TableCell,TableHead,TableHeader,TableRow,
} from '@redbamboo/ui'
import {
  createLocalStore,useLocalStore,type LeafPluginExtensionProps,
} from '@redbamboo/utility'
import {createDevice,current,listAgents,listDevices,simulate,updateDevice} from './api'
import type {Agent,Device,FaceGesture,FaceState,Snapshot} from './types'

const states:FaceState[]=['dark','resting','listening','thinking','speaking','working','dreaming','asleep']
const novaSettings=createLocalStore<{agentFilter:string|null}>('nova_settings',{agentFilter:null})

export function PixooNovaSection(_props:LeafPluginExtensionProps){
  const {agentFilter}=useLocalStore(novaSettings)
  return <PixooPage scopedAgentId={agentFilter} embedded/>
}

export function PixooPage({scopedAgentId,embedded=false}:{scopedAgentId?:string|null;embedded?:boolean}={}){
  const [agents,setAgents]=useState<Agent[]>([])
  const [devices,setDevices]=useState<Device[]>([])
  const [selected,setSelected]=useState('')
  const [draftAgent,setDraftAgent]=useState('')
  const [agentPickerOpen,setAgentPickerOpen]=useState(false)
  const [view,setView]=useState<Snapshot|null>(null)
  const [frame,setFrame]=useState(0)
  const [busy,setBusy]=useState(false)
  const [error,setError]=useState('')
  const scoped=embedded
  const visibleDevices=useMemo(
    ()=>scoped?devices.filter(item=>item.agentId===scopedAgentId):devices,
    [devices,scoped,scopedAgentId],
  )
  const device=devices.find(item=>item.id===selected)
  const selectedAgent=agents.find(agent=>agent.id===(scoped?scopedAgentId:device?.agentId??draftAgent))

  useEffect(()=>{
    let alive=true
    void Promise.all([listAgents(),listDevices()]).then(([nextAgents,nextDevices])=>{
      if(!alive)return
      setAgents(nextAgents)
      setDraftAgent(nextAgents[0]?.id??'')
      setDevices(nextDevices)
    }).catch(reason=>setError(message(reason)))
    return()=>{alive=false}
  },[])

  useEffect(()=>{
    setSelected(previous=>visibleDevices.some(item=>item.id===previous)?previous:visibleDevices[0]?.id??'')
  },[visibleDevices])

  useEffect(()=>{
    if(!selected){setView(null);return}
    void current(selected).then(setView).catch(reason=>setError(message(reason)))
  },[selected])

  useEffect(()=>{
    if(!view?.frames.length)return
    const duration=view.frames[frame%view.frames.length]?.durationMs??500
    const timer=window.setTimeout(()=>setFrame(value=>(value+1)%view.frames.length),duration)
    return()=>window.clearTimeout(timer)
  },[view,frame])

  useEffect(()=>setFrame(0),[view?.state,view?.gesture])

  const pixels=useMemo(
    ()=>decode(view?.frames[frame%Math.max(1,view.frames.length)]?.pixels),
    [view,frame],
  )

  const act=async(state:FaceState,gesture?:FaceGesture)=>{
    if(!selected)return
    setBusy(true);setError('')
    try{setView(await simulate(selected,state,gesture))}
    catch(reason){setError(message(reason))}
    finally{setBusy(false)}
  }

  const save=async(patch:Partial<Device>)=>{
    if(!device)return
    const next={...device,...patch}
    setDevices(items=>items.map(item=>item.id===next.id?next:item))
    try{
      const saved=await updateDevice(next)
      setDevices(items=>items.map(item=>item.id===saved.id?saved:item))
      setView(await current(saved.id))
    }catch(reason){setError(message(reason))}
  }

  const bindAgent=(agent:Agent)=>{
    setDraftAgent(agent.id)
    setAgentPickerOpen(false)
    if(device)void save({agentId:agent.id})
  }

  const add=async()=>{
    const agentId=scoped?scopedAgentId:draftAgent
    if(!agentId)return
    setBusy(true);setError('')
    try{
      const made=await createDevice(agentId)
      setDevices(items=>[...items.filter(item=>item.id!==made.id),made])
      setSelected(made.id)
      setView(await current(made.id))
    }catch(reason){setError(message(reason))}
    finally{setBusy(false)}
  }

  return <main className="min-h-full bg-background text-foreground" data-ui-surface={embedded?'pixoo-nova-section':'pixoo-emulator'}>
    <PanelHeader
      title={embedded?'Display':'Pixoo face'}
      leading={<i className="ph-bold ph-monitor-play text-primary" aria-hidden="true"/>}
    >
      <Badge variant="outline">64 × 64</Badge>
    </PanelHeader>

    {scoped&&!scopedAgentId?<div className="mx-auto max-w-xl p-6 sm:p-10">
      <Card>
        <CardHeader><CardTitle>Choose an Agent in Nova</CardTitle><CardDescription>The Display section follows Nova's current Agent filter.</CardDescription></CardHeader>
      </Card>
    </div>:<div className="mx-auto grid max-w-7xl items-start gap-5 p-3 sm:p-6 lg:grid-cols-[minmax(22rem,1.35fr)_minmax(19rem,.8fr)] lg:gap-8">
      <section className="flex min-w-0 flex-col items-center" aria-label="LED matrix emulator">
        <div className="pixoo-bezel">
          <div className="pixoo-matrix" role="img" aria-label={`${view?.state??'dark'} pixel face`}>
            {pixels.map((color,index)=><i className="pixoo-led" key={index} style={{'--led':color} as CSSProperties}/>) }
          </div>
          <div className="pixoo-bezel-foot"><span className="pixoo-speaker"/><b>PIXOO</b><span className="pixoo-power"/></div>
        </div>
        <div className="mt-4 flex items-center gap-2 text-sm capitalize text-text-muted" aria-live="polite">
          <span className="size-2 rounded-full bg-primary shadow-[0_0_8px_var(--primary)]" aria-hidden="true"/>
          <Badge variant="secondary"><span data-testid="current-state">{view?.state??'dark'}</span></Badge>
          {view?.gesture&&<Badge variant="outline">gesture · {view.gesture}</Badge>}
        </div>
      </section>

      <aside className="flex min-w-0 flex-col gap-4" aria-label="Pixoo controls">
        <Card size="sm">
          <CardHeader>
            <CardTitle>Display binding</CardTitle>
            <CardDescription>{embedded?'Devices bound to Nova’s selected Agent.':'Choose the device and Agent represented by this face.'}</CardDescription>
            <CardAction><Badge variant={device?.kind==='pixoo64'?'outline':'secondary'}>{device?.kind??'no device'}</Badge></CardAction>
          </CardHeader>
          <CardContent className="space-y-4">
            {visibleDevices.length>0&&<div className="space-y-2">
              <Label>Device</Label>
              <Select value={selected} onValueChange={value=>setSelected(typeof value==='string'?value:'')}>
                <SelectTrigger className="w-full" aria-label="Device"><SelectValue>{value=>visibleDevices.find(item=>item.id===value)?.name??String(value??'')}</SelectValue></SelectTrigger>
                <SelectContent>{visibleDevices.map(item=><SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}</SelectContent>
              </Select>
            </div>}

            {selectedAgent?<div className="space-y-2">
              <Label>Agent</Label>
              <EntityCard
                entity={{id:selectedAgent.id,typeSlug:'agent',name:selectedAgent.name}}
                visual={{src:selectedAgent.avatarUrl,fallbackText:selectedAgent.name,shape:'circle'}}
                subtitle={embedded?'Selected in Nova':'Bound Agent'}
                variant="outlined"
                actions={!embedded?<Button variant="outline" size="sm" onClick={()=>setAgentPickerOpen(true)}>Change</Button>:undefined}
              />
            </div>:!embedded&&<Button variant="outline" className="w-full" onClick={()=>setAgentPickerOpen(true)}>Choose Agent</Button>}

            {device&&<div className="space-y-3">
              <div className="flex items-center justify-between gap-3"><Label htmlFor="pixoo-brightness">Brightness</Label><output className="text-xs tabular-nums text-text-muted">{device.brightness}%</output></div>
              <Slider id="pixoo-brightness" aria-label="Brightness" min={0} max={100} value={[device.brightness]} onValueChange={value=>void save({brightness:Array.isArray(value)?value[0]??0:value})}/>
            </div>}

            {!device&&<p className="rounded-lg border border-dashed border-overlay-6 p-4 text-center text-sm text-text-muted">No display is bound to this Agent.</p>}
            <Button variant="outline" className="w-full" disabled={busy||!selectedAgent} onClick={()=>void add()}>
              <i className="ph-bold ph-plus" aria-hidden="true"/> Add emulator
            </Button>
          </CardContent>
        </Card>

        <Card size="sm">
          <CardHeader><CardTitle>Simulate</CardTitle><CardDescription>Force a state or brief gesture without producing content.</CardDescription></CardHeader>
          <CardContent className="space-y-4">
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-2 xl:grid-cols-4">
              {states.map(state=><Button key={state} size="sm" variant={view?.state===state&&!view?.gesture?'secondary':'outline'} disabled={busy||!device} aria-pressed={view?.state===state&&!view?.gesture} onClick={()=>void act(state)} className="capitalize">{state}</Button>)}
            </div>
            <div>
              <Label className="mb-2">Gesture</Label>
              <div className="grid grid-cols-2 gap-2">
                {(['arrive','react'] as FaceGesture[]).map(gesture=><Button key={gesture} size="sm" variant="outline" disabled={busy||!device} onClick={()=>void act(view?.state??'resting',gesture)} className="capitalize">{gesture}</Button>)}
              </div>
            </div>
          </CardContent>
        </Card>

        <Card size="sm">
          <CardHeader><CardTitle>Recent pushes</CardTitle><CardDescription>Event types only. No conversation content is stored or shown.</CardDescription></CardHeader>
          <CardContent className="px-0!">
            <Table>
              <TableHeader><TableRow><TableHead>Time</TableHead><TableHead>State</TableHead><TableHead>Cause</TableHead></TableRow></TableHeader>
              <TableBody>{view?.history.length?view.history.slice(0,6).map((entry,index)=><TableRow key={`${entry.pushedAt}-${index}`}>
                <TableCell className="whitespace-nowrap font-mono text-xs tabular-nums tracking-normal text-text-muted">{new Date(entry.pushedAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}</TableCell>
                <TableCell><Badge variant="outline" className="capitalize">{entry.state}</Badge></TableCell>
                <TableCell className="max-w-36 truncate font-mono text-xs text-text-muted">{entry.cause}</TableCell>
              </TableRow>):<TableRow><TableCell colSpan={3} className="text-center text-text-muted">No pushes yet</TableCell></TableRow>}</TableBody>
            </Table>
          </CardContent>
        </Card>

        {error&&<Card size="sm" role="alert" className="ring-destructive-a20"><CardContent className="text-destructive">{error}</CardContent></Card>}
      </aside>
    </div>}

    {!embedded&&<AgentPicker open={agentPickerOpen} agents={agents} selectedId={device?.agentId??draftAgent} onOpenChange={setAgentPickerOpen} onSelect={bindAgent}/>}
  </main>
}

function AgentPicker({open,agents,selectedId,onOpenChange,onSelect}:{
  open:boolean;agents:Agent[];selectedId:string;onOpenChange:(open:boolean)=>void;onSelect:(agent:Agent)=>void
}){
  return <Dialog open={open} onOpenChange={onOpenChange}>
    <DialogContent>
      <DialogHeader><DialogTitle>Choose an Agent</DialogTitle><DialogDescription>The display will represent exactly this Agent.</DialogDescription></DialogHeader>
      <ScrollArea className="max-h-96">
        <div className="space-y-2 pr-2">{agents.map(agent=><EntityCard
          key={agent.id}
          entity={{id:agent.id,typeSlug:'agent',name:agent.name}}
          visual={{src:agent.avatarUrl,fallbackText:agent.name,shape:'circle'}}
          subtitle="Leaf Agent"
          variant="outlined"
          selected={agent.id===selectedId}
          action={{kind:'button',ariaLabel:`Select ${agent.name}`,onActivate:()=>onSelect(agent)}}
          trailing={agent.id===selectedId?<Badge variant="secondary">Current</Badge>:undefined}
        />)}</div>
      </ScrollArea>
    </DialogContent>
  </Dialog>
}

function message(value:unknown){return value instanceof Error?value.message:String(value)}

function decode(value?:string){
  if(!value)return Array.from({length:4096},()=> 'rgb(0 0 0)')
  const raw=atob(value),result:string[]=[]
  for(let i=0;i<raw.length;i+=3)result.push(`rgb(${raw.charCodeAt(i)} ${raw.charCodeAt(i+1)} ${raw.charCodeAt(i+2)})`)
  return result
}
