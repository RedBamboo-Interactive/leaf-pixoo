import {useEffect,useMemo,useRef} from 'react'
import type {Frame} from './types'

const SIZE=64

/**
 * The physical LED panel, drawn on one canvas. Each frame is three compositing operations
 * (pixels, glow, LED mask) instead of 4,096 styled DOM nodes, so animation stays cheap.
 */
export function LedMatrix({frames,frame,label}:{frames:Frame[];frame:number;label:string}){
  const canvas=useRef<HTMLCanvasElement>(null)
  const decoded=useMemo(()=>frames.map(toImageData),[frames])
  const source=useMemo(()=>{
    const element=document.createElement('canvas')
    element.width=SIZE;element.height=SIZE
    return element
  },[])
  const mask=useRef<{size:number;canvas:HTMLCanvasElement}|null>(null)

  useEffect(()=>{
    const target=canvas.current
    if(!target)return
    const observer=new ResizeObserver(()=>{
      const css=target.clientWidth
      const scale=Math.max(1,Math.round(css*(window.devicePixelRatio||1)/SIZE))
      const size=scale*SIZE
      if(target.width!==size){target.width=size;target.height=size}
      draw()
    })
    observer.observe(target)
    return()=>observer.disconnect()
  })

  useEffect(()=>draw())

  function draw(){
    const target=canvas.current
    const context=target?.getContext('2d')
    if(!target||!context)return
    const size=target.width
    const image=decoded.length?decoded[frame%decoded.length]:null
    const sourceContext=source.getContext('2d')!
    if(image)sourceContext.putImageData(image,0,0)
    else{sourceContext.fillStyle='#000';sourceContext.fillRect(0,0,SIZE,SIZE)}

    context.globalCompositeOperation='source-over'
    context.globalAlpha=1
    context.filter='none'
    context.fillStyle='#020303'
    context.fillRect(0,0,size,size)
    context.imageSmoothingEnabled=false
    context.drawImage(source,0,0,size,size)
    context.globalCompositeOperation='destination-in'
    context.drawImage(ledMask(size),0,0)
    context.globalCompositeOperation='lighter'
    context.globalAlpha=.35
    context.imageSmoothingEnabled=true
    context.filter=`blur(${Math.max(1,size/SIZE*.45)}px) saturate(1.2)`
    context.drawImage(source,0,0,size,size)
    context.globalCompositeOperation='source-over'
    context.globalAlpha=1
    context.filter='none'
  }

  function ledMask(size:number){
    if(mask.current?.size===size)return mask.current.canvas
    const element=document.createElement('canvas')
    element.width=size;element.height=size
    const context=element.getContext('2d')!
    const pitch=size/SIZE
    const led=pitch*.86
    const radius=led*.44
    context.fillStyle='#fff'
    for(let y=0;y<SIZE;y++)for(let x=0;x<SIZE;x++){
      context.beginPath()
      context.roundRect(x*pitch+(pitch-led)/2,y*pitch+(pitch-led)/2,led,led,radius)
      context.fill()
    }
    mask.current={size,canvas:element}
    return element
  }

  return <canvas ref={canvas} className="pixoo-matrix" role="img" aria-label={label}/>
}

function toImageData(frame:Frame){
  if(frame.imageData)return frame.imageData
  const data=new ImageData(SIZE,SIZE)
  const value=frame.pixels
  if(!value)return data
  const raw=atob(value)
  for(let source=0,target=0;source<raw.length&&target<data.data.length;source+=3,target+=4){
    data.data[target]=raw.charCodeAt(source)
    data.data[target+1]=raw.charCodeAt(source+1)
    data.data[target+2]=raw.charCodeAt(source+2)
    data.data[target+3]=255
  }
  return data
}
