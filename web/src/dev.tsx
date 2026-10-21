import {StrictMode} from 'react'
import {createRoot} from 'react-dom/client'
import {PixooNovaSection,PixooPage} from './PixooPage'
import './dev.css'

const params=new URLSearchParams(location.search)
const theme=params.get('theme')==='dark'?'dark':'light'
document.documentElement.classList.toggle('dark',theme==='dark')
document.documentElement.style.colorScheme=theme
const content=params.get('surface')==='nova'
  ?<div className="min-h-screen bg-background p-3 sm:p-6"><div className="mx-auto min-h-[720px] max-w-6xl overflow-hidden rounded-xl border border-overlay-6 bg-surface shadow-sm"><PixooNovaSection context={{targetPluginId:'nova',slotId:'app-section'}}/></div></div>
  :<PixooPage/>
createRoot(document.getElementById('root')!).render(<StrictMode>{content}</StrictMode>)
