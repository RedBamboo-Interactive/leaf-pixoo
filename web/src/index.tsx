import type {LeafAppPlugin} from '@redbamboo/utility'
import {PixooNovaSection,PixooPage} from './PixooPage'
import './plugin.css'

export const plugin:LeafAppPlugin={
  id:'pixoo',
  Page:PixooPage,
  extensions:{display:PixooNovaSection},
}
