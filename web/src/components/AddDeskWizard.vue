<!-- SPDX-License-Identifier: MIT -->
<!-- Add-desk wizard: Windows user, password, resolution and FPS. Emits the new desk config; the parent creates it. -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { DeskConfig, HostUser } from '../api/types'
import { validateDeskConfig } from '../api/validate'
import { FPS_PRESETS, newDeskConfig, RESOLUTION_PRESETS, resolutionKey } from '../desk'
import { t } from '../i18n'
import AppIcon from './AppIcon.vue'
import ModalDialog from './ModalDialog.vue'

const props = withDefaults(
  defineProps<{
    open: boolean
    /** Local accounts from GET /host/users; empty = unknown, the name is typed. */
    users?: HostUser[]
    /** Desks that exist: their ids and users are taken. */
    desks?: ReadonlyArray<{ id: string; user: string }>
    busy?: boolean
    error?: string | null
  }>(),
  { users: () => [], desks: () => [], busy: false, error: null },
)
const emit = defineEmits<{ close: []; create: [config: DeskConfig] }>()

const NEW = '\u0000new'
const step = ref(0)
const pick = ref<string | null>(null)
const newName = ref('')
const password = ref('')
const res = ref(resolutionKey(1920, 1080))
const customW = ref(1920)
const customH = ref(1080)
const fps = ref(60)
const tried = ref(false)

function reset(): void {
  step.value = 0
  pick.value = props.users.length === 0 ? NEW : null
  newName.value = ''
  password.value = ''
  res.value = resolutionKey(1920, 1080)
  customW.value = 1920
  customH.value = 1080
  fps.value = 60
  tried.value = false
}
watch(
  () => props.open,
  (open) => {
    if (open) reset()
  },
  { immediate: true },
)

const usedUsers = computed(() => new Set(props.desks.map((d) => d.user.toLowerCase())))
function findUser(name: string): HostUser | undefined {
  const n = name.trim().toLowerCase()
  return props.users.find((u) => u.name.toLowerCase() === n)
}
function userBlocked(u: HostUser): boolean {
  return u.isConsoleUser || usedUsers.value.has(u.name.toLowerCase())
}

/** The chosen account name. */
const userName = computed(() => (pick.value === NEW ? newName.value.trim() : (pick.value ?? '')))
/** The chosen account exists on the PC (then its password is required). */
const existing = computed(() => findUser(userName.value))

const width = computed(() => (res.value === 'custom' ? Number(customW.value) : Number(res.value.split('x')[0])))
const height = computed(() => (res.value === 'custom' ? Number(customH.value) : Number(res.value.split('x')[1])))

const config = computed(() =>
  newDeskConfig({
    user: userName.value,
    fullName: existing.value?.fullName,
    password: password.value,
    width: width.value,
    height: height.value,
    fps: fps.value,
    existingIds: props.desks.map((d) => d.id),
  }),
)
const errors = computed(() => validateDeskConfig(config.value))

const userError = computed(() => {
  if (!userName.value) return pick.value === NEW ? t('form.err.user') : null
  if (errors.value.user) return t(errors.value.user)
  const u = existing.value
  if (u && userBlocked(u)) return u.isConsoleUser ? t('wizard.console') : t('wizard.taken')
  if (usedUsers.value.has(userName.value.toLowerCase())) return t('wizard.taken')
  return null
})
const passwordError = computed(() => (existing.value && password.value === '' ? t('form.err.passwordRequired') : null))
const displayError = computed(() => {
  const key = errors.value.resolution ?? errors.value.fps
  return key ? t(key) : null
})

const stepValid = computed(() => {
  if (step.value === 0) return userName.value !== '' && userError.value === null
  if (step.value === 1) return passwordError.value === null
  return displayError.value === null
})

const STEPS = ['wizard.stepUser', 'wizard.stepPassword', 'wizard.stepDisplay'] as const

function next(): void {
  tried.value = true
  if (!stepValid.value || props.busy) return
  tried.value = false
  if (step.value < 2) step.value++
  else emit('create', config.value)
}

function back(): void {
  tried.value = false
  if (step.value > 0) step.value--
}

function choose(name: string): void {
  pick.value = name
  tried.value = false
}

function initial(name: string): string {
  return (name.trim()[0] ?? '?').toUpperCase()
}
</script>

<template>
  <ModalDialog :open="open" :title="t('wizard.title')" :width="620" @close="emit('close')">
    <ol class="steps">
      <li v-for="(key, i) in STEPS" :key="key" :class="{ active: i === step, done: i < step }">
        <span class="num"><AppIcon v-if="i < step" name="check" :size="14" /><template v-else>{{ i + 1 }}</template></span>
        <span class="step-label">{{ t(key) }}</span>
      </li>
    </ol>

    <form class="wizard" novalidate @submit.prevent="next">
      <!-- 1. Windows user -->
      <section v-if="step === 0" class="pane" data-step="user">
        <h3>{{ t('wizard.userTitle') }}</h3>
        <div class="users" role="radiogroup" :aria-label="t('desk.user')">
          <button
            v-for="u in users"
            :key="u.name"
            type="button"
            role="radio"
            :aria-checked="pick === u.name"
            :class="['user', { selected: pick === u.name }]"
            :data-user="u.name"
            :disabled="userBlocked(u)"
            @click="choose(u.name)"
          >
            <span class="avatar">{{ initial(u.fullName || u.name) }}</span>
            <span class="who">
              <span class="name">{{ u.name }}</span>
              <span v-if="u.isConsoleUser" class="tag">{{ t('wizard.console') }}</span>
              <span v-else-if="usedUsers.has(u.name.toLowerCase())" class="tag">{{ t('wizard.taken') }}</span>
              <span v-else-if="u.fullName" class="full">{{ u.fullName }}</span>
            </span>
          </button>
          <button
            v-if="users.length > 0"
            type="button"
            role="radio"
            :aria-checked="pick === NEW"
            :class="['user', 'new', { selected: pick === NEW }]"
            data-user-new
            @click="choose(NEW)"
          >
            <span class="avatar plus"><AppIcon name="plus" :size="18" /></span>
            <span class="who"><span class="name">{{ t('wizard.newUser') }}</span></span>
          </button>
        </div>
        <div v-if="pick === NEW" class="field">
          <label for="wiz-user">{{ users.length ? t('wizard.newUser') : t('desk.user') }}</label>
          <input
            id="wiz-user"
            v-model="newName"
            name="newUser"
            maxlength="20"
            autocomplete="off"
            spellcheck="false"
            :placeholder="t('wizard.newUserPlaceholder')"
            :aria-invalid="tried && !!userError"
          />
        </div>
        <p v-if="tried && (userError || !userName)" class="err" role="alert">{{ userError ?? t('wizard.pickUser') }}</p>
      </section>

      <!-- 2. Password -->
      <section v-else-if="step === 1" class="pane" data-step="password">
        <h3>{{ t('wizard.passwordTitle') }}</h3>
        <div class="account">
          <span class="avatar">{{ initial(existing?.fullName || userName) }}</span>
          <span class="who">
            <span class="name">{{ userName }}</span>
            <span v-if="existing?.fullName" class="full">{{ existing.fullName }}</span>
          </span>
        </div>
        <div class="field">
          <label for="wiz-password">{{ t('adv.password') }}</label>
          <input
            id="wiz-password"
            v-model="password"
            name="password"
            type="password"
            maxlength="256"
            autocomplete="off"
            autofocus
            :aria-invalid="tried && !!passwordError"
          />
          <span v-if="tried && passwordError" class="err" role="alert">{{ passwordError }}</span>
          <span v-else class="hint">{{ existing ? t('wizard.passwordHint') : t('wizard.passwordNewHint') }}</span>
        </div>
      </section>

      <!-- 3. Resolution and FPS -->
      <section v-else class="pane" data-step="display">
        <h3>{{ t('wizard.displayTitle') }}</h3>
        <div class="field-label label">{{ t('wizard.resolution') }}</div>
        <div class="res-grid" role="radiogroup" :aria-label="t('wizard.resolution')">
          <button
            v-for="p in RESOLUTION_PRESETS"
            :key="resolutionKey(p.width, p.height)"
            type="button"
            role="radio"
            :aria-checked="res === resolutionKey(p.width, p.height)"
            :class="['tile', { selected: res === resolutionKey(p.width, p.height) }]"
            :data-res="resolutionKey(p.width, p.height)"
            @click="res = resolutionKey(p.width, p.height)"
          >
            <span class="tile-main">{{ p.width }}×{{ p.height }}</span>
            <span class="tile-tag">{{ p.tag }}</span>
          </button>
          <button
            type="button"
            role="radio"
            :aria-checked="res === 'custom'"
            :class="['tile', { selected: res === 'custom' }]"
            data-res="custom"
            @click="res = 'custom'"
          >
            <span class="tile-main">{{ t('desk.custom') }}</span>
            <span class="tile-tag">W×H</span>
          </button>
        </div>
        <div v-if="res === 'custom'" class="custom">
          <input v-model.number="customW" name="width" type="number" min="320" max="7680" :aria-label="t('desk.width')" />
          <span class="muted">×</span>
          <input v-model.number="customH" name="height" type="number" min="240" max="4320" :aria-label="t('desk.height')" />
        </div>

        <div class="field-label label fps-label">{{ t('wizard.fps') }}</div>
        <div class="segmented" role="radiogroup" :aria-label="t('wizard.fps')">
          <button
            v-for="f in FPS_PRESETS"
            :key="f"
            type="button"
            role="radio"
            :aria-checked="fps === f"
            :class="{ selected: fps === f }"
            :data-fps="f"
            @click="fps = f"
          >
            {{ f }}
          </button>
        </div>
        <p v-if="tried && displayError" class="err" role="alert">{{ displayError }}</p>
      </section>

      <p v-if="error" class="inline-msg" role="alert">{{ error }}</p>

      <footer>
        <button v-if="step > 0" type="button" class="btn-ghost" data-back @click="back">
          <AppIcon name="chevronLeft" :size="16" />{{ t('common.back') }}
        </button>
        <span class="spacer" />
        <button v-if="step < 2" type="submit" class="btn-primary next" data-next>{{ t('common.next') }}</button>
        <button v-else type="submit" class="btn-play create" data-create :disabled="busy">
          <AppIcon name="play" :size="16" />{{ t('wizard.create') }}
        </button>
      </footer>
    </form>
  </ModalDialog>
</template>

<style scoped>
.steps {
  list-style: none;
  margin: 0 0 22px;
  padding: 0;
  display: flex;
  gap: 8px;
  counter-reset: none;
}

.steps li {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 10px;
  padding-bottom: 10px;
  border-bottom: 2px solid rgb(255 255 255 / 8%);
  color: var(--dim);
  font-size: 13px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  min-width: 0;
  transition:
    color 0.2s ease,
    border-color 0.2s ease;
}

.steps li.active {
  color: #fff;
  border-color: var(--accent);
}

.steps li.done {
  color: var(--accent);
  border-color: rgb(102 192 244 / 45%);
}

.num {
  width: 24px;
  height: 24px;
  border-radius: 50%;
  display: grid;
  place-items: center;
  flex: none;
  background: rgb(255 255 255 / 8%);
  font-size: 12px;
}

.active .num {
  background: var(--accent);
  color: #0e1a26;
}

.done .num {
  background: rgb(102 192 244 / 20%);
}

.step-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wizard {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.pane {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-height: 236px;
}

.pane h3 {
  font-size: 18px;
  font-weight: 400;
  color: #fff;
}

.users {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
}

.user,
.account {
  display: flex;
  align-items: center;
  gap: 12px;
  height: auto;
  min-height: 58px;
  padding: 10px 12px;
  text-align: left;
  background: rgb(0 0 0 / 20%);
  border: 1px solid rgb(255 255 255 / 6%);
  border-radius: 3px;
  white-space: normal;
  justify-content: flex-start;
}

.user:hover:not(:disabled) {
  background: rgb(102 192 244 / 10%);
  border-color: rgb(102 192 244 / 35%);
}

.user.selected {
  background: rgb(102 192 244 / 16%);
  border-color: var(--accent);
  box-shadow: 0 0 0 1px var(--accent) inset;
}

.user:disabled {
  opacity: 0.4;
}

.avatar {
  width: 36px;
  height: 36px;
  flex: none;
  border-radius: 3px;
  display: grid;
  place-items: center;
  background: linear-gradient(135deg, #2a8bc9, #1f4e79);
  color: #fff;
  font-weight: 700;
  font-size: 16px;
}

.avatar.plus {
  background: rgb(255 255 255 / 8%);
  color: var(--accent);
}

.who {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.name {
  color: #fff;
  font-weight: 700;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.full {
  font-size: 12px;
  color: var(--muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tag {
  align-self: flex-start;
  margin-top: 2px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding: 1px 6px;
  border-radius: 2px;
  background: rgb(229 177 67 / 18%);
  color: var(--warn);
}

.res-grid {
  display: grid;
  grid-template-columns: repeat(5, 1fr);
  gap: 8px;
}

.tile {
  height: 64px;
  flex-direction: column;
  gap: 2px;
  padding: 0 6px;
  background: rgb(0 0 0 / 20%);
  border: 1px solid rgb(255 255 255 / 6%);
  border-radius: 3px;
}

.tile:hover:not(:disabled) {
  background: rgb(102 192 244 / 10%);
  border-color: rgb(102 192 244 / 35%);
}

.tile.selected {
  background: rgb(102 192 244 / 16%);
  border-color: var(--accent);
  box-shadow: 0 0 0 1px var(--accent) inset;
}

.tile-main {
  color: #fff;
  font-weight: 700;
  font-size: 14px;
  font-variant-numeric: tabular-nums;
}

.tile-tag {
  font-size: 11px;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.selected .tile-tag {
  color: var(--accent);
}

.custom {
  display: flex;
  align-items: center;
  gap: 10px;
}

.custom input {
  width: 110px;
}

.fps-label {
  margin-top: 4px;
}

.segmented {
  display: inline-flex;
  background: rgb(0 0 0 / 25%);
  border-radius: 3px;
  padding: 3px;
  gap: 3px;
  align-self: flex-start;
}

.segmented button {
  height: 34px;
  min-width: 64px;
  background: transparent;
  color: var(--muted);
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}

.segmented button:hover:not(:disabled) {
  background: rgb(255 255 255 / 6%);
}

.segmented button.selected {
  background: linear-gradient(90deg, #06bfff 0%, #2d73ff 100%);
  color: #fff;
}

.inline-msg {
  margin: 0;
}

footer {
  display: flex;
  align-items: center;
  gap: 10px;
  padding-top: 16px;
  border-top: 1px solid rgb(255 255 255 / 6%);
}

.next {
  min-width: 130px;
  height: 40px;
}

.create {
  min-width: 220px;
}

@media (width <= 560px) {
  .res-grid {
    grid-template-columns: repeat(3, 1fr);
  }

  .steps .step-label {
    display: none;
  }

  .create {
    min-width: 0;
  }
}
</style>
