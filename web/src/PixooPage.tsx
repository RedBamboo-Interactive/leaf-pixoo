import {useEffect,useState} from 'react'
import {
  Badge,Button,Card,CardAction,CardContent,CardDescription,CardHeader,CardTitle,
  Dialog,DialogContent,DialogDescription,DialogHeader,DialogTitle,EntityCard,Input,Label,
  ItemList,ItemListRow,MasterDetailLayout,PanelHeader,ScrollArea,Slider,Switch,
  Table,TableBody,TableCell,TableHead,TableHeader,TableRow,
} from '@redbamboo/ui'
import type {LeafPluginExtensionProps} from '@redbamboo/utility'
import {createDevice,current,currentGif,displayState,listAgents,listDevices,portraitStatus,pushHistory,simulate,updateDevice} from './api'
import type {Agent,Device,DisplayState,FaceGesture,FaceState,Frame,PortraitStatus,Snapshot} from './types'
import {LedMatrix} from './LedMatrix'

const states:FaceState[]=['dark','resting','listening','thinking','speaking','working','dreaming','asleep']
type PickerMode='create'|'bind'

export function PixooNovaSection(_props:LeafPluginExtensionProps){
  return <PixooPage embedded/>
}

export function PixooPage({embedded=false}:{embedded?:boolean}={}){
  const [agents,setAgents]=useState<Agent[]>([])
  const [devices,setDevices]=useState<Device[]>([])
  const [selected,setSelected]=useState('')
  const [mobileTab,setMobileTab]=useState(0)
  const [name,setName]=useState('')
  const [pickerMode,setPickerMode]=useState<PickerMode>('bind')
  const [agentPickerOpen,setAgentPickerOpen]=useState(false)
  const [view,setView]=useState<Snapshot|null>(null)
  const [live,setLive]=useState<{deviceId:string;value:DisplayState}|null>(null)
  const [portrait,setPortrait]=useState<PortraitStatus|null>(null)
  const [frame,setFrame]=useState(0)
  const [loading,setLoading]=useState(true)
  const [busy,setBusy]=useState(false)
  const [error,setError]=useState('')
  const device=devices.find(item=>item.id===selected)
  const selectedAgent=agents.find(agent=>agent.id===device?.agentId)

  useEffect(()=>{
    let alive=true
    void Promise.all([listAgents(),listDevices()]).then(([nextAgents,nextDevices])=>{
      if(!alive)return
      setAgents(nextAgents)
      setDevices(nextDevices)
    }).catch(reason=>setError(message(reason))).finally(()=>{if(alive)setLoading(false)})
    return()=>{alive=false}
  },[])

  useEffect(()=>{
    if(selected&&devices.some(item=>item.id===selected))return
    // Open the first display on desktop; on phone only jump to the detail tab when there is one.
    const next=devices[0]?.id??''
    setSelected(next)
    if(next&&devices.length===1)setMobileTab(1)
  },[devices,selected])

  useEffect(()=>setName(device?.name??''),[device?.id,device?.name])

  useEffect(()=>{
    const agentId=device?.agentId
    if(!agentId){setPortrait(null);return}
    setPortrait(null)
    let alive=true
    const refresh=()=>void portraitStatus(agentId).then(value=>{if(alive)setPortrait(value)}).catch(()=>{})
    refresh()
    const timer=window.setInterval(refresh,5000)
    return()=>{alive=false;window.clearInterval(timer)}
  },[device?.agentId])

  useEffect(()=>{
    if(!selected||!device){setView(null);setLive(null);return}
    setView({device,state:'dark',frames:[],history:[],playback:{}})
    setLive(null)
  },[selected,device?.id])

  useEffect(()=>{
    if(!selected)return
    let alive=true
    let timer:number|undefined
    const refresh=async()=>{
      if(document.hidden||!alive)return
      try{
        const [state,history]=await Promise.all([displayState(selected),pushHistory(selected)])
        if(!alive)return
        setLive({deviceId:selected,value:state})
        setView(value=>value?{...value,state:state.state,gesture:state.gesture,history,playback:state.playback}:value)
      }catch(reason){if(alive)setError(message(reason))}
    }
    const start=()=>{
      if(timer!==undefined)window.clearInterval(timer)
      timer=undefined
      if(document.hidden||!alive)return
      void refresh()
      timer=window.setInterval(()=>void refresh(),1500)
    }
    const visibility=()=>start()
    document.addEventListener('visibilitychange',visibility)
    start()
    return()=>{alive=false;if(timer!==undefined)window.clearInterval(timer);document.removeEventListener('visibilitychange',visibility)}
  },[selected])

  const liveState=live?.deviceId===selected?live.value:null

  useEffect(()=>{
    const loopKey=liveState?.loopKey
    if(!selected||!loopKey)return
    let alive=true
    void (async()=>{
      let frames:Frame[]|null=null
      if(imageDecoderAvailable()){
        const gif=await currentGif(selected)
        if(gif)try{frames=await decodeGif(gif,liveState.frameDurationMs,liveState.frameCount)}catch{frames=null}
      }
      if(!frames)frames=(await current(selected)).frames
      if(alive)setView(value=>value?{...value,frames}:value)
    })().catch(reason=>{if(alive)setError(message(reason))})
    return()=>{alive=false}
  },[selected,liveState?.loopKey])

  useEffect(()=>{
    if(!view?.frames.length)return
    const duration=view.frames[frame%view.frames.length]?.durationMs??500
    const timer=window.setTimeout(()=>setFrame(value=>(value+1)%view.frames.length),duration)
    return()=>window.clearTimeout(timer)
  },[view,frame])

  useEffect(()=>setFrame(0),[liveState?.loopKey,selected])

  const selectDisplay=(id:string)=>{
    setSelected(id)
    setMobileTab(1)
  }

  const act=async(state:FaceState,gesture?:FaceGesture)=>{
    if(!selected)return
    setBusy(true);setError('')
    try{
      const next=await simulate(selected,state,gesture)
      setLive({deviceId:selected,value:next})
      setView(value=>value?{...value,state:next.state,gesture:next.gesture,playback:next.playback}:value)
    }
    catch(reason){setError(message(reason))}
    finally{setBusy(false)}
  }

  const save=async(patch:Partial<Device>)=>{
    if(!device)return
    const previous=device
    const next={...device,...patch}
    setDevices(items=>items.map(item=>item.id===next.id?next:item))
    setError('')
    try{
      const saved=await updateDevice(next)
      setDevices(items=>items.map(item=>item.id===saved.id?saved:item))
    }catch(reason){
      setDevices(items=>items.map(item=>item.id===previous.id?previous:item))
      setError(message(reason))
    }
  }

  const commitName=()=>{
    const trimmed=name.trim()
    if(!device)return
    if(!trimmed){setName(device.name);return}
    if(trimmed!==device.name)void save({name:trimmed})
  }

  const openPicker=(mode:PickerMode)=>{
    setPickerMode(mode)
    setAgentPickerOpen(true)
  }

  const chooseAgent=async(agent:Agent)=>{
    setAgentPickerOpen(false)
    if(pickerMode==='bind'){
      await save({agentId:agent.id})
      return
    }
    setBusy(true);setError('')
    try{
      const made=await createDevice(agent.id,'Display '+(devices.length+1))
      setDevices(items=>[...items.filter(item=>item.id!==made.id),made])
      selectDisplay(made.id)
    }catch(reason){setError(message(reason))}
    finally{setBusy(false)}
  }

  const openCreate=()=>openPicker('create')

  const sidebar=<section className="flex h-full min-h-0 flex-col bg-surface" aria-label="Displays">
    <PanelHeader
      title="Displays"
      leading={<i className="ph-bold ph-monitor-play text-text-muted" aria-hidden="true"/>}
    >
      <Badge variant="secondary">{devices.length}</Badge>
      <Button size="icon-sm" variant="ghost" aria-label="Add display" disabled={busy||agents.length===0} onClick={openCreate}>
        <i className="ph-bold ph-plus" aria-hidden="true"/>
      </Button>
    </PanelHeader>
    {error&&<div role="alert" className="mx-3 mt-3 rounded-md bg-destructive-a10 px-3 py-2 text-sm text-destructive">{error}</div>}
    {loading?<div className="flex flex-1 items-center justify-center p-6 text-sm text-text-muted">Loading displays…</div>
    :devices.length===0?<div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
      <div className="flex size-10 items-center justify-center rounded-lg bg-overlay-5 text-text-muted">
        <i className="ph-bold ph-monitor-play text-xl" aria-hidden="true"/>
      </div>
      <div>
        <p className="font-medium">No displays yet</p>
        <p className="mt-1 max-w-52 text-sm text-text-muted">Add an emulator, then choose the Agent it should represent.</p>
      </div>
      <Button size="sm" disabled={busy||agents.length===0} onClick={openCreate}>
        <i className="ph-bold ph-plus" aria-hidden="true"/> Add display
      </Button>
      {agents.length===0&&<p className="text-xs text-text-muted">An Agent is required before a display can be added.</p>}
    </div>:<>
      <div className="px-4 pb-1 pt-3 text-[11px] font-semibold uppercase tracking-wide text-text-muted">Configured displays</div>
      <ItemList
        className="min-h-0 flex-1"
        items={devices}
        keyFn={item=>item.id}
        renderItem={item=>{
          const agent=agents.find(candidate=>candidate.id===item.agentId)
          return <ItemListRow
            selected={item.id===selected}
            onClick={()=>selectDisplay(item.id)}
            icon={agent?.avatarUrl
              ?<img src={agent.avatarUrl} alt="" className="size-full rounded-full object-cover"/>
              :<i className="ph-bold ph-monitor-play text-text-muted" aria-hidden="true"/>}
            title={item.name}
            subtitle={(item.kind==='pixoo64'?'Pixoo-64':'Emulator')+' · '+(agent?.name??'Unknown Agent')}
            trailing={<><span className="sr-only">{item.id===selected?'Selected display':'Display configured'}</span><span className={'size-2 rounded-full '+(item.id===selected?'bg-accent-teal':'bg-overlay-15')} aria-hidden="true"/></>}
          />
        }}
      />
      <div className="border-t border-border p-3">
        <Button variant="outline" className="w-full" disabled={busy||agents.length===0} onClick={openCreate}>
          <i className="ph-bold ph-plus" aria-hidden="true"/> Add display
        </Button>
      </div>
    </>}
  </section>

  return <main className={(embedded?'h-full':'h-dvh')+' min-h-0 bg-background text-foreground'} data-ui-surface={embedded?'pixoo-nova-section':'pixoo-emulator'}>

    <MasterDetailLayout
      layoutKey="pixoo-displays"
      mobileLabels={['Displays','Display']}
      mobileTab={mobileTab}
      onMobileTabChange={setMobileTab}
      sidebar={sidebar}
      detail={!device?<section className="flex h-full min-h-0 items-center justify-center bg-background p-8 text-center" aria-label="Display detail">
        <div className="max-w-xs text-text-muted">
          <i className="ph-bold ph-monitor-play text-3xl" aria-hidden="true"/>
          <p className="mt-3 font-medium text-foreground">Select a display</p>
          <p className="mt-1 text-sm">Choose a display from the list to preview and configure it.</p>
        </div>
      </section>:<section className="flex h-full min-h-0 flex-col bg-background" aria-label="Display detail">
        <PanelHeader title={device.name} leading={<i className="ph-bold ph-monitor-play text-text-muted" aria-hidden="true"/>}>
          <Badge variant={device.kind==='pixoo64'?'outline':'secondary'}>{device.kind==='pixoo64'?'Pixoo-64':'Emulator'}</Badge>
        </PanelHeader>
        <ScrollArea className="min-h-0 flex-1">
    <div className="mx-auto grid w-full max-w-6xl items-start gap-4 p-3 sm:p-5 xl:grid-cols-[minmax(20rem,1.1fr)_minmax(20rem,.9fr)]">
      <section className="flex min-w-0 flex-col items-center xl:sticky xl:top-5" aria-label="LED matrix emulator">
        <div className="pixoo-bezel">
          <LedMatrix frames={view?.frames??[]} frame={frame} label={`${view?.state??'dark'} pixel face`}/>
          <div className="pixoo-bezel-foot"><span className="pixoo-speaker"/><b>PIXOO</b><span className="pixoo-power"/></div>
        </div>
        <div className="mt-4 flex flex-wrap items-center justify-center gap-2 text-sm capitalize text-text-muted" aria-live="polite">
          <span className="inline-flex items-center gap-1.5 text-xs font-medium text-success"><span className="size-2 animate-pulse rounded-full bg-success" aria-hidden="true"/>Live</span>
          <Badge variant="secondary"><span data-testid="current-state">{view?.state??'dark'}</span></Badge>
          {view?.gesture&&<Badge variant="outline">gesture · {view.gesture}</Badge>}
          {view?.playback?.playingGif&&<span className="text-xs">{view.playback.playingGif}</span>}
          {view?.playback?.lastFetchedAt&&<span className="text-xs tabular-nums">fetched {new Date(view.playback.lastFetchedAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}</span>}
        </div>
      </section>

      <aside className="flex min-w-0 flex-col gap-4" aria-label="Pixoo controls">
        <Card size="sm">
          <CardHeader>
            <CardTitle>Display settings</CardTitle>
            <CardDescription>This display represents exactly one Agent.</CardDescription>
            <CardAction><Badge variant={device?.kind==='pixoo64'?'outline':'secondary'}>{device?.kind??'display'}</Badge></CardAction>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="pixoo-device-name">Name</Label>
              <Input id="pixoo-device-name" value={name} maxLength={80} disabled={busy}
                onChange={event=>setName(event.target.value)} onBlur={commitName}
                onKeyDown={event=>{if(event.key==='Enter')event.currentTarget.blur()}}/>
            </div>

            <div className="space-y-2">
              <Label>Agent</Label>
              {selectedAgent?<EntityCard
                entity={{id:selectedAgent.id,typeSlug:'agent',name:selectedAgent.name}}
                visual={{src:selectedAgent.avatarUrl,fallbackText:selectedAgent.name,shape:'circle'}}
                subtitle="Bound Agent"
                variant="outlined"
                actions={<Button variant="outline" size="sm" onClick={()=>openPicker('bind')}>Change</Button>}
              />:<Button variant="outline" className="w-full" onClick={()=>openPicker('bind')}>Choose Agent</Button>}
            </div>

            {portrait&&<div className="space-y-2">
              <div className="flex items-center justify-between gap-3"><Label>Portrait clips</Label><Badge variant="outline" className="capitalize">{portrait.state.replace('-', ' ')}</Badge></div>
              <div className="flex flex-wrap gap-1.5" aria-label="Portrait clip generation status">
                {portrait.clips.map(clip=><Badge key={clip.key??clip.state} variant={clip.phase==='ready'?'secondary':'outline'} className="capitalize">{(clip.key??clip.state).replaceAll('-',' ')} · {clip.phase}{clip.frozenBackgroundFraction!=null&&clip.phase==='ready'?` · ${Math.round(clip.frozenBackgroundFraction*100)}% background frozen`:''}</Badge>)}
              </div>
            </div>}

            {device&&<div className="space-y-3">
              <div className="flex items-center justify-between gap-3"><Label htmlFor="pixoo-brightness">Brightness</Label><output className="text-xs tabular-nums text-text-muted">{device.brightness}%</output></div>
              <Slider id="pixoo-brightness" aria-label="Brightness" min={0} max={100} value={[device.brightness]} onValueChange={value=>void save({brightness:Array.isArray(value)?value[0]??0:value})}/>
            </div>}

            {device?.kind==='pixoo64'&&<div className="space-y-2 border-t border-border pt-4">
              <div className="flex items-center justify-between gap-4">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                    <Label htmlFor="pixoo-hardware-enabled">Show on device</Label>
                    {!device.hardwareEnabled?<span className="inline-flex items-center gap-1.5 text-xs text-text-muted" aria-live="polite">
                      <span className="size-2 rounded-full bg-overlay-20" aria-hidden="true"/>Off
                    </span>:view?.playback?.health&&<span className="inline-flex items-center gap-1.5 text-xs text-text-muted" aria-live="polite">
                      <span className={'size-2 rounded-full '+(view.playback.health.state==='offline'?'bg-destructive':'bg-success')} aria-hidden="true"/>
                      <span className="capitalize">{view.playback.health.state}</span>
                      {view.playback.health.lastProbeAt&&<span className="tabular-nums">· checked {new Date(view.playback.health.lastProbeAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</span>}
                      {view.playback.health.state==='recovered'&&view.playback.health.lastRecoveryAt&&<span className="tabular-nums">· restored {new Date(view.playback.health.lastRecoveryAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</span>}
                      {view.playback.health.state==='offline'&&view.playback.health.consecutiveFailures>1&&<span>· {view.playback.health.consecutiveFailures} failed probes</span>}
                    </span>}
                  </div>
                  <p className="mt-1 text-xs text-text-muted">Allow Leaf to drive this physical display.</p>
                </div>
                <Switch id="pixoo-hardware-enabled" checked={device.hardwareEnabled} disabled={busy}
                  onCheckedChange={value=>void save({hardwareEnabled:value})}/>
              </div>
            </div>}

            <div className="space-y-2 border-t border-border pt-4">
              <div className="flex items-center justify-between gap-3">
                <Label>Device playback</Label>
                <Badge variant="outline">{view?.playback?.playingGif??'Not delivered'}</Badge>
              </div>
              {view?.playback?.lastFetchedAt&&<p className="text-xs text-text-muted">
                Fetched {new Date(view.playback.lastFetchedAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}
                {view.playback.lastFetchedFrom?' from '+view.playback.lastFetchedFrom:''}
              </p>}
              {view?.playback?.deliveryError&&<p role="alert" className="rounded-md bg-destructive-a10 px-3 py-2 text-xs text-destructive">{view.playback.deliveryError}</p>}
            </div>
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
                <TableCell className="whitespace-nowrap text-xs tabular-nums tracking-normal text-text-muted">{new Date(entry.pushedAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}</TableCell>
                <TableCell><Badge variant="outline" className="capitalize">{entry.state}</Badge></TableCell>
                <TableCell className="max-w-36 truncate font-mono text-xs text-text-muted">{entry.cause}</TableCell>
              </TableRow>):<TableRow><TableCell colSpan={3} className="text-center text-text-muted">No pushes yet</TableCell></TableRow>}</TableBody>
            </Table>
          </CardContent>
        </Card>

      </aside>
    </div>
        </ScrollArea>
      </section>}
    />

    <AgentPicker open={agentPickerOpen} agents={agents} selectedId={pickerMode==='bind'?device?.agentId:undefined}
      mode={pickerMode} onOpenChange={setAgentPickerOpen} onSelect={agent=>void chooseAgent(agent)}/>
  </main>
}

function AgentPicker({open,agents,selectedId,mode,onOpenChange,onSelect}:{
  open:boolean;agents:Agent[];selectedId?:string;mode:PickerMode;onOpenChange:(open:boolean)=>void;onSelect:(agent:Agent)=>void
}){
  return <Dialog open={open} onOpenChange={onOpenChange}>
    <DialogContent>
      <DialogHeader><DialogTitle>{mode==='create'?'Add display':'Choose an Agent'}</DialogTitle><DialogDescription>{mode==='create'?'Choose the Agent for this emulator.':'The selected display will represent exactly this Agent.'}</DialogDescription></DialogHeader>
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

type GifDecoder={tracks:{ready:Promise<void>;selectedTrack?:{frameCount:number}};decode:(options:{frameIndex:number;completeFramesOnly?:boolean})=>Promise<{image:CanvasImageSource&{close?:()=>void}}> ;close:()=>void}
type GifDecoderConstructor=new(options:{data:ArrayBuffer;type:string})=>GifDecoder

function imageDecoderAvailable(){return typeof (window as unknown as {ImageDecoder?:GifDecoderConstructor}).ImageDecoder==='function'}

async function decodeGif(blob:Blob,durationMs:number,expectedFrameCount:number):Promise<Frame[]>{
  const Decoder=(window as unknown as {ImageDecoder?:GifDecoderConstructor}).ImageDecoder
  if(!Decoder)throw new Error('Animated GIF decoding is unavailable')
  const decoder=new Decoder({data:await blob.arrayBuffer(),type:'image/gif'})
  try{
    await decoder.tracks.ready
    const count=decoder.tracks.selectedTrack?.frameCount||expectedFrameCount
    if(count<1)throw new Error('GIF contains no frames')
    const canvas=document.createElement('canvas');canvas.width=64;canvas.height=64
    const context=canvas.getContext('2d',{willReadFrequently:true})!
    const frames:Frame[]=[]
    for(let index=0;index<count;index++){
      const decoded=await decoder.decode({frameIndex:index,completeFramesOnly:true})
      context.clearRect(0,0,64,64)
      context.drawImage(decoded.image,0,0,64,64)
      frames.push({imageData:context.getImageData(0,0,64,64),durationMs})
      decoded.image.close?.()
    }
    return frames
  }finally{decoder.close()}
}

