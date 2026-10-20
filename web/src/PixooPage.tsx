import {useCallback,useEffect,useMemo,useState} from 'react'
import {createDevice,current,listAgents,listDevices,simulate,updateDevice} from './api'
import type {Agent,Device,FaceGesture,FaceState,Snapshot} from './types'
import './plugin.css'

const states:FaceState[]=['dark','resting','listening','thinking','speaking','working','dreaming','asleep']

export function PixooPage(){
  const [agents,setAgents]=useState<Agent[]>([]),[devices,setDevices]=useState<Device[]>([]),[selected,setSelected]=useState('')
  const [draftAgent,setDraftAgent]=useState('')
  const [view,setView]=useState<Snapshot|null>(null),[frame,setFrame]=useState(0),[busy,setBusy]=useState(false),[error,setError]=useState('')
  const device=devices.find(item=>item.id===selected)
  const load=useCallback(async()=>{try{const [a,d]=await Promise.all([listAgents(),listDevices()]);setAgents(a);setDraftAgent(value=>value||a[0]?.id||'');setDevices(d);const id=selected||d[0]?.id||'';setSelected(id);if(id)setView(await current(id))}catch(e){setError(e instanceof Error?e.message:String(e))}},[selected])
  useEffect(()=>{void load()},[])
  useEffect(()=>{if(!selected)return;void current(selected).then(setView).catch(e=>setError(String(e)))},[selected])
  useEffect(()=>{if(!view?.frames.length)return;const duration=view.frames[frame%view.frames.length]?.durationMs??500;const timer=setTimeout(()=>setFrame(value=>(value+1)%view.frames.length),duration);return()=>clearTimeout(timer)},[view,frame])
  useEffect(()=>setFrame(0),[view?.state,view?.gesture])
  const pixels=useMemo(()=>decode(view?.frames[frame%Math.max(1,view.frames.length)]?.pixels),[view,frame])
  const act=async(state:FaceState,gesture?:FaceGesture)=>{if(!selected)return;setBusy(true);setError('');try{setView(await simulate(selected,state,gesture))}catch(e){setError(e instanceof Error?e.message:String(e))}finally{setBusy(false)}}
  const save=async(patch:Partial<Device>)=>{if(!device)return;const next={...device,...patch};setDevices(list=>list.map(item=>item.id===next.id?next:item));try{const saved=await updateDevice(next);setDevices(list=>list.map(item=>item.id===saved.id?saved:item));setView(await current(saved.id))}catch(e){setError(e instanceof Error?e.message:String(e))}}
  const add=async()=>{if(!draftAgent)return;setBusy(true);try{const made=await createDevice(draftAgent);setDevices(list=>[...list,made]);setSelected(made.id);setView(await current(made.id))}catch(e){setError(e instanceof Error?e.message:String(e))}finally{setBusy(false)}}
  return <main className="pixoo-page" data-ui-surface="pixoo-emulator">
    <header className="pixoo-header"><div><span className="eyebrow">Leaf peripheral</span><h1>Pixoo face</h1></div><span className="quiet-mark">64 × 64</span></header>
    <div className="pixoo-layout">
      <section className="device-stage" aria-label="LED matrix emulator">
        <div className="bezel"><div className="matrix" role="img" aria-label={`${view?.state??'dark'} pixel face`}>
          {pixels.map((color,index)=><i key={index} style={{'--led':color} as React.CSSProperties}/>) }
        </div><div className="bezel-foot"><span></span><b>PIXOO</b><span className="power-dot"></span></div></div>
        <div className="state-readout"><span className="live-dot"></span><strong>{view?.state??'dark'}</strong>{view?.gesture&&<small>gesture · {view.gesture}</small>}</div>
      </section>
      <aside className="controls" aria-label="Pixoo controls">
        <div className="control-block selectors">
          <label>Device<select aria-label="Device" value={selected} onChange={e=>setSelected(e.target.value)}>{devices.map(d=><option value={d.id} key={d.id}>{d.name}</option>)}</select></label>
          <label>Agent<select aria-label="Agent" value={device?.agentId??draftAgent} onChange={e=>device?void save({agentId:e.target.value}):setDraftAgent(e.target.value)}>{agents.map(a=><option value={a.id} key={a.id}>{a.name}</option>)}</select></label>
          <label className="brightness"><span>Brightness <output>{device?.brightness??0}%</output></span><input aria-label="Brightness" type="range" min="0" max="100" value={device?.brightness??0} onChange={e=>void save({brightness:Number(e.target.value)})}/></label>
          <button className="add-device" disabled={busy||!draftAgent} onClick={()=>void add()}>+ Add emulator</button>
        </div>
        <div className="control-block"><h2>Simulate state</h2><div className="state-grid">{states.map(state=><button key={state} disabled={busy} aria-pressed={view?.state===state&&!view?.gesture} onClick={()=>void act(state)}>{state}</button>)}</div>
          <h2 className="gesture-title">Gesture</h2><div className="gesture-row"><button disabled={busy} onClick={()=>void act(view?.state??'resting','arrive')}>arrive</button><button disabled={busy} onClick={()=>void act(view?.state??'resting','react')}>react</button></div>
        </div>
        <div className="control-block push-log"><div className="log-title"><h2>Recent pushes</h2><span>event types only</span></div>
          <ol>{view?.history.length?view.history.slice(0,6).map((entry,i)=><li key={`${entry.pushedAt}-${i}`}><time>{new Date(entry.pushedAt).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}</time><span>{entry.state}</span><code>{entry.cause}</code></li>):<li className="empty">No pushes yet</li>}</ol>
        </div>{error&&<p role="alert" className="error">{error}</p>}
      </aside>
    </div>
  </main>
}

function decode(value?:string){
  if(!value)return Array.from({length:4096},()=> 'rgb(0 0 0)')
  const raw=atob(value),result:string[]=[]
  for(let i=0;i<raw.length;i+=3)result.push(`rgb(${raw.charCodeAt(i)} ${raw.charCodeAt(i+1)} ${raw.charCodeAt(i+2)})`)
  return result
}
