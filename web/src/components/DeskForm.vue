<!-- SPDX-License-Identifier: MIT -->
<!-- Create/edit form for a desk config (docs/contracts/config.md `desks[]`). -->
<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { CODECS, GPU_PRIORITIES, type DeskConfig, type GpuInfo } from '../api/types'
import { FPS_MAX, FPS_MIN, hasErrors, validateDeskConfig, type DeskConfigErrors } from '../api/validate'
import { t } from '../i18n'

const props = withDefaults(
  defineProps<{
    initial?: DeskConfig | null
    /** Desk is running: only the profile may change (web-api.md, PUT /desks/{id}). */
    running?: boolean
    gpus?: GpuInfo[]
    existingIds?: string[]
    busy?: boolean
  }>(),
  { initial: null, running: false, gpus: () => [], existingIds: () => [], busy: false },
)
const emit = defineEmits<{ submit: [config: DeskConfig]; cancel: [] }>()

const RESOLUTIONS: ReadonlyArray<readonly [number, number]> = [
  [1280, 720],
  [1600, 900],
  [1920, 1080],
  [2560, 1080],
  [2560, 1440],
  [3440, 1440],
  [3840, 2160],
]
const FPS_PRESETS = [30, 60, 90, 120, 144, 165, 240, 360, 480, 500]

const editing = computed(() => props.initial !== null)
const locked = computed(() => editing.value && props.running)

function defaults(): DeskConfig {
  return {
    id: '',
    name: '',
    user: '',
    autostart: false,
    gpu: 'auto',
    profile: { fps: 120, width: 1920, height: 1080, bitrateKbps: 60000, codec: 'hevc' },
  }
}

const init: DeskConfig = props.initial ? JSON.parse(JSON.stringify(props.initial)) : defaults()
const form = reactive({
  id: init.id,
  name: init.name,
  user: init.user,
  autostart: init.autostart,
  gpu: init.gpu,
  basePort: init.basePort === undefined ? '' : String(init.basePort),
  width: init.profile.width,
  height: init.profile.height,
  fps: init.profile.fps,
  bitrateMbps: init.profile.bitrateKbps / 1000,
  codec: init.profile.codec,
  fecPercent: init.profile.fecPercent,
  minFps: init.profile.minFps,
  gpuPriority: init.profile.gpuPriority,
})
// Fields the form does not edit (dpbFrames, pacingMbps, …) are kept as they were.
const passthrough = { ...init.profile }

const resolutionKey = ref(
  RESOLUTIONS.some(([w, h]) => w === form.width && h === form.height) ? `${form.width}x${form.height}` : 'custom',
)
function onResolution(): void {
  if (resolutionKey.value === 'custom') return
  const [w, h] = resolutionKey.value.split('x').map(Number)
  form.width = w ?? form.width
  form.height = h ?? form.height
}

const submitted = ref(false)

function toConfig(): DeskConfig {
  const port = form.basePort.trim()
  const profile: DeskConfig['profile'] = {
    ...passthrough,
    fps: Number(form.fps),
    width: Number(form.width),
    height: Number(form.height),
    bitrateKbps: Math.round(Number(form.bitrateMbps) * 1000),
    codec: form.codec,
  }
  if (form.fecPercent === undefined || String(form.fecPercent) === '') delete profile.fecPercent
  else profile.fecPercent = Number(form.fecPercent)
  if (form.minFps === undefined || String(form.minFps) === '') delete profile.minFps
  else profile.minFps = Number(form.minFps)
  if (form.gpuPriority) profile.gpuPriority = form.gpuPriority
  else delete profile.gpuPriority
  const config: DeskConfig = {
    id: form.id.trim(),
    name: form.name.trim(),
    user: form.user.trim(),
    autostart: form.autostart,
    gpu: form.gpu,
    profile,
  }
  if (port !== '') config.basePort = Number(port)
  return config
}

const errors = computed<DeskConfigErrors>(() =>
  validateDeskConfig(
    toConfig(),
    props.existingIds.filter((id) => id !== props.initial?.id),
  ),
)

function err(field: keyof DeskConfigErrors): string | null {
  const key = errors.value[field]
  return submitted.value && key ? t(key) : null
}

function submit(): void {
  submitted.value = true
  if (hasErrors(errors.value) || props.busy) return
  emit('submit', toConfig())
}

// Unknown LUIDs (adapter removed) stay selectable so that saving does not silently change the GPU.
const gpuOptions = computed(() => {
  const list = props.gpus.map((g) => ({ value: g.luid, label: `${g.name} (${g.luid})` }))
  if (form.gpu !== 'auto' && !list.some((g) => g.value === form.gpu)) list.push({ value: form.gpu, label: form.gpu })
  return list
})
</script>

<template>
  <form class="card desk-form" novalidate @submit.prevent="submit">
    <p v-if="locked" class="notice warn">{{ t('form.runningNotice') }}</p>

    <div class="grid-2">
      <div class="field">
        <label for="desk-id">{{ t('form.id') }}</label>
        <input
          id="desk-id"
          v-model="form.id"
          name="id"
          class="mono"
          maxlength="32"
          autocomplete="off"
          spellcheck="false"
          :disabled="editing"
          :aria-invalid="!!err('id')"
        />
        <span v-if="err('id')" class="error">{{ err('id') }}</span>
        <span v-else class="hint">{{ t('form.idHint') }}</span>
      </div>
      <div class="field">
        <label for="desk-name">{{ t('form.name') }}</label>
        <input id="desk-name" v-model="form.name" name="name" maxlength="64" :disabled="locked" :aria-invalid="!!err('name')" />
        <span v-if="err('name')" class="error">{{ err('name') }}</span>
      </div>
      <div class="field">
        <label for="desk-user">{{ t('form.user') }}</label>
        <input
          id="desk-user"
          v-model="form.user"
          name="user"
          maxlength="20"
          autocomplete="off"
          spellcheck="false"
          :disabled="locked"
          :aria-invalid="!!err('user')"
        />
        <span v-if="err('user')" class="error">{{ err('user') }}</span>
        <span v-else class="hint">{{ t('form.userHint') }}</span>
      </div>
      <div class="field">
        <label for="desk-gpu">{{ t('form.gpu') }}</label>
        <select id="desk-gpu" v-model="form.gpu" name="gpu" :disabled="locked">
          <option value="auto">{{ t('form.gpuAuto') }}</option>
          <option v-for="g in gpuOptions" :key="g.value" :value="g.value">{{ g.label }}</option>
        </select>
      </div>
      <div class="field">
        <label for="desk-port">{{ t('form.basePort') }}</label>
        <input
          id="desk-port"
          v-model="form.basePort"
          name="basePort"
          inputmode="numeric"
          placeholder="48089"
          :disabled="locked"
          :aria-invalid="!!err('basePort')"
        />
        <span v-if="err('basePort')" class="error">{{ err('basePort') }}</span>
        <span v-else class="hint">{{ t('form.basePortHint') }}</span>
      </div>
      <div class="check">
        <input id="desk-autostart" v-model="form.autostart" type="checkbox" name="autostart" :disabled="locked" />
        <label for="desk-autostart">{{ t('form.autostart') }}</label>
      </div>
    </div>

    <h2>{{ t('desk.profile') }}</h2>
    <div class="grid-2">
      <div class="field">
        <label for="desk-res">{{ t('form.resolution') }}</label>
        <select id="desk-res" v-model="resolutionKey" name="resolution" @change="onResolution">
          <option v-for="[w, h] in RESOLUTIONS" :key="`${w}x${h}`" :value="`${w}x${h}`">{{ w }} × {{ h }}</option>
          <option value="custom">{{ t('form.resolutionCustom') }}</option>
        </select>
        <div v-if="resolutionKey === 'custom'" class="row">
          <input v-model.number="form.width" name="width" type="number" min="320" max="7680" :aria-label="t('form.width')" />
          ×
          <input v-model.number="form.height" name="height" type="number" min="240" max="4320" :aria-label="t('form.height')" />
        </div>
        <span v-if="err('resolution')" class="error">{{ err('resolution') }}</span>
      </div>
      <div class="field">
        <label for="desk-fps">{{ t('form.fps') }}</label>
        <input
          id="desk-fps"
          v-model.number="form.fps"
          name="fps"
          type="number"
          :min="FPS_MIN"
          :max="FPS_MAX"
          list="fps-presets"
          :aria-invalid="!!err('fps')"
        />
        <datalist id="fps-presets">
          <option v-for="f in FPS_PRESETS" :key="f" :value="f" />
        </datalist>
        <span v-if="err('fps')" class="error">{{ err('fps') }}</span>
        <span v-else class="hint">{{ t('form.fpsHint') }}</span>
      </div>
      <div class="field">
        <label for="desk-bitrate">{{ t('form.bitrate') }}</label>
        <input
          id="desk-bitrate"
          v-model.number="form.bitrateMbps"
          name="bitrate"
          type="number"
          min="0.5"
          max="1000"
          step="0.5"
          :aria-invalid="!!err('bitrate')"
        />
        <span v-if="err('bitrate')" class="error">{{ err('bitrate') }}</span>
      </div>
      <div class="field">
        <label for="desk-codec">{{ t('form.codec') }}</label>
        <select id="desk-codec" v-model="form.codec" name="codec">
          <option v-for="c in CODECS" :key="c" :value="c">{{ c.toUpperCase() }}</option>
        </select>
      </div>
    </div>

    <details>
      <summary>{{ t('form.advanced') }}</summary>
      <div class="grid-2 advanced">
        <div class="field">
          <label for="desk-fec">{{ t('form.fecPercent') }}</label>
          <input id="desk-fec" v-model.number="form.fecPercent" name="fecPercent" type="number" min="0" max="100" placeholder="20" />
          <span v-if="err('fecPercent')" class="error">{{ err('fecPercent') }}</span>
        </div>
        <div class="field">
          <label for="desk-minfps">{{ t('form.minFps') }}</label>
          <input id="desk-minfps" v-model.number="form.minFps" name="minFps" type="number" min="1" placeholder="15" />
          <span v-if="err('minFps')" class="error">{{ err('minFps') }}</span>
        </div>
        <div class="field">
          <label for="desk-prio">{{ t('form.gpuPriority') }}</label>
          <select id="desk-prio" v-model="form.gpuPriority" name="gpuPriority">
            <option :value="undefined">{{ t('common.none') }}</option>
            <option v-for="p in GPU_PRIORITIES" :key="p" :value="p">{{ p }}</option>
          </select>
        </div>
      </div>
    </details>

    <div class="row actions">
      <div class="spacer" />
      <button type="button" @click="emit('cancel')">{{ t('common.cancel') }}</button>
      <button type="submit" class="primary" :disabled="busy">{{ editing ? t('common.save') : t('form.create') }}</button>
    </div>
  </form>
</template>

<style scoped>
.desk-form h2 {
  margin-top: 0.5rem;
}

.advanced {
  margin-top: 0.75rem;
}

.actions {
  margin-top: 1rem;
}

details summary {
  cursor: pointer;
  color: var(--muted);
}
</style>
