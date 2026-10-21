import type {Agent,Device,FaceGesture,FaceState,Frame,Snapshot} from './types'

declare const __PIXOO_HAS_LOCAL_PREVIEW__:boolean

const base='/api/extensions/pixoo'
const params=new URLSearchParams(location.search)
const harness=location.port==='4173' || params.has('harness')
const ids={device:'11111111-1111-1111-1111-111111111111',second:'44444444-4444-4444-4444-444444444444',agent:'22222222-2222-2222-2222-222222222222',secondAgent:'33333333-3333-3333-3333-333333333333'}
const agents:Agent[]=[{id:ids.agent,name:'Demo Agent',color:'#9a7cff'},{id:ids.secondAgent,name:'Second Agent',color:'#78e6c4'}]
const primary:Device={id:ids.device,name:'Living room',kind:'emulator',brightness:82,agentId:ids.agent,agentColor:'#9a7cff',minimumPushGapMs:1000,hardwareEnabled:false}
const secondary:Device={id:ids.second,name:'Studio shelf',kind:'emulator',brightness:68,agentId:ids.secondAgent,agentColor:'#78e6c4',minimumPushGapMs:1000,hardwareEnabled:false}
const scenario=params.get('scenario')??'one'
let devices:Device[]=scenario==='empty'?[]:scenario==='two'?[primary,secondary]:[primary]
const snapshots=new Map<string,Snapshot>(devices.map(device=>[device.id,makeSnapshot(device)]))
let preview:Partial<Record<FaceState,Frame[]>>|null=null
let previewRequest:Promise<void>|null=null

async function json<T>(path:string,init?:RequestInit):Promise<T>{
  // Same-origin session auth, like other Nova sections: an app execution token is minted per route,
  // and this page also renders inside Nova's /apps/nova/display, which does not belong to 'pixoo'.
  const response=await fetch(base+path,{...init,credentials:'same-origin',headers:{'Content-Type':'application/json',...(init?.headers??{})}})
  if(!response.ok)throw new Error(`${response.status} ${response.statusText}`)
  return response.json() as Promise<T>
}
export async function listAgents(){return harness?agents:json<Agent[]>('/agents')}
export async function listDevices(){return harness?devices:json<Device[]>('/devices')}
export async function current(deviceId:string){
  if(!harness)return json<Snapshot>(`/emulator?deviceId=${encodeURIComponent(deviceId)}`)
  await loadPreview()
  const snapshot=snapshots.get(deviceId)
  if(!snapshot)throw new Error('Display not found')
  const next={...snapshot,frames:preview?.[snapshot.state]??snapshot.frames}
  snapshots.set(deviceId,next)
  return next
}
export async function updateDevice(value:Device){
  if(harness){
    const saved={...value,agentColor:agents.find(agent=>agent.id===value.agentId)?.color??value.agentColor}
    devices=devices.map(device=>device.id===saved.id?saved:device)
    const snapshot=snapshots.get(saved.id)??makeSnapshot(saved)
    snapshots.set(saved.id,{...snapshot,device:saved})
    return saved
  }
  return json<Device>(`/devices/${value.id}`,{method:'PUT',body:JSON.stringify(value)})
}
export async function createDevice(agentId:string,name='Display'){
  const value={name,kind:'emulator' as const,brightness:74,agentId,agentColor:agents.find(agent=>agent.id===agentId)?.color??'#7c5cff',minimumPushGapMs:1000,hardwareEnabled:false}
  if(harness){
    await loadPreview()
    const made={id:crypto.randomUUID(),...value}
    devices=[...devices,made]
    snapshots.set(made.id,{...makeSnapshot(made),frames:preview?.resting??render('resting',made.agentColor,made.brightness)})
    return made
  }
  return json<Device>('/devices',{method:'POST',body:JSON.stringify(value)})
}
export async function simulate(deviceId:string,state:FaceState,gesture?:FaceGesture){
  if(harness){
    await loadPreview()
    const device=devices.find(item=>item.id===deviceId)
    if(!device)throw new Error('Display not found')
    const previous=snapshots.get(deviceId)??makeSnapshot(device)
    const frames=gesture?render(state,device.agentColor,device.brightness,gesture):preview?.[state]??render(state,device.agentColor,device.brightness)
    const snapshot={device,state,gesture,frames,history:[{pushedAt:new Date().toISOString(),state,gesture,cause:gesture??'simulate',frameCount:frames.length},...previous.history].slice(0,8)}
    snapshots.set(deviceId,snapshot)
    return snapshot
  }
  return json<Snapshot>(`/devices/${deviceId}/simulate`,{method:'POST',body:JSON.stringify({state,gesture})})
}

function makeSnapshot(device:Device):Snapshot{
  return {device,state:'resting',frames:render('resting',device.agentColor,device.brightness),history:[{pushedAt:new Date().toISOString(),state:'resting',cause:'device.created',frameCount:4}]}
}

async function loadPreview(){
  if(preview||!harness)return
  if(!__PIXOO_HAS_LOCAL_PREVIEW__){preview={};return}
  previewRequest??=fetch('/portrait-preview.json').then(async response=>{
    if(!response.ok)throw new Error(`portrait preview ${response.status}`)
    const body=await response.json() as {states:Partial<Record<FaceState,Frame[]>>}
    preview=body.states
  }).catch(()=>{preview={}})
  await previewRequest
}

function render(state:FaceState,color:string,brightness:number,gesture?:FaceGesture):Frame[]{
  const levels=state==='dark'?[0]:state==='resting'?[.45,.53,.62,.53]:state==='speaking'?[.75,.95,.82,.95]:state==='thinking'?[.58,.74,.9,.74]:[.65,.82]
  const duration=state==='speaking'?180:state==='thinking'?280:state==='resting'?700:450
  return levels.map((level,phase)=>{
    const rgb=hex(color),bytes=new Uint8Array(64*64*3),light=brightness/100
    if(state!=='dark'){
      for(let y=0;y<64;y++)for(let x=0;x<64;x++){
        const dx=(x-31.5)/45,dy=(y-31.5)/45,v=Math.max(0,1-dx*dx-dy*dy),drift=.82+.18*Math.sin((x+y+phase*4)/13)
        put(bytes,x,y,rgb,.075*level*v*drift*light)
      }
      const blink=state==='resting'&&phase===levels.length-1,eyeH=state==='asleep'||blink?1:state==='listening'?8:6,shift=state==='thinking'?phase-2:0
      eye(bytes,20+shift,25,8,eyeH,rgb,level*light);eye(bytes,44+shift,25,8,eyeH,rgb,level*light)
      const mouthH=state==='speaking'?(phase%2?6:2):state==='asleep'?1:2
      for(let y=-Math.floor(mouthH/2);y<=Math.floor(mouthH/2);y++)for(let x=-9;x<=9;x++)if(Math.abs(x)+Math.abs(y)<10)put(bytes,32+x,42+y,rgb,level*light)
      if(gesture==='react')for(const [cx,cy] of [[8,12],[55,10]])for(let d=-4;d<=4;d++){put(bytes,cx+d,cy,rgb,light);put(bytes,cx,cy+d,rgb,light)}
    }
    let raw=''; for(let i=0;i<bytes.length;i+=8192)raw+=String.fromCharCode(...bytes.subarray(i,i+8192))
    return {pixels:btoa(raw),durationMs:duration}
  })
}
function hex(value:string):[number,number,number]{const n=parseInt(value.replace('#',''),16);return[(n>>16)&255,(n>>8)&255,n&255]}
function put(p:Uint8Array,x:number,y:number,c:[number,number,number],l:number){if(x<0||x>63||y<0||y>63)return;const i=(Math.round(y)*64+Math.round(x))*3;p[i]=c[0]*l;p[i+1]=c[1]*l;p[i+2]=c[2]*l}
function eye(p:Uint8Array,cx:number,cy:number,w:number,h:number,c:[number,number,number],l:number){for(let y=-h/2;y<=h/2;y++)for(let x=-w/2;x<=w/2;x++)if(x*x/(w*w)+y*y/Math.max(1,h*h)<=.28)put(p,cx+x,cy+y,c,l)}
