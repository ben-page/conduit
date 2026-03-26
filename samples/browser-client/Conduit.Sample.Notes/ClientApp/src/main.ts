import { ConduitClient, SignalRTransport } from '../generated/conduit.generated';

const transport = new SignalRTransport();
const conduit = new ConduitClient(transport);

await transport.start();
document.getElementById('status')!.textContent = 'Connected';

async function loadNotes() {
  const result = await conduit.fetch('Conduit.Sample.Notes.Queries.GetNotesQuery', {});
  if (!result.data) return;
  const list = document.getElementById('noteList')!;
  list.replaceChildren();
  for (const note of result.data) {
    const li = document.createElement('li');
    const span = document.createElement('span');
    span.textContent = note.text;
    const btn = document.createElement('button');
    btn.textContent = 'delete';
    btn.addEventListener('click', () => deleteNote(note.id));
    li.appendChild(span);
    li.appendChild(btn);
    list.appendChild(li);
  }
}

async function createNote() {
  const input = document.getElementById('noteText') as HTMLInputElement;
  const text = input.value.trim();
  if (!text) return;
  input.value = '';
  const result = await conduit.execute('Conduit.Sample.Notes.Commands.CreateNoteCommand', { text });
  if (result.error) {
    logEvent(`Error: [${result.error.code}] ${result.error.message}`);
  }
}

async function deleteNote(id: string) {
  await conduit.execute('Conduit.Sample.Notes.Commands.DeleteNoteCommand', { id });
}

conduit.on('Conduit.Sample.Notes.Events.NoteCreatedEvent', event => {
  logEvent(`NoteCreated: ${event.text}`);
  loadNotes();
});

conduit.on('Conduit.Sample.Notes.Events.NoteDeletedEvent', event => {
  logEvent(`NoteDeleted: ${event.id}`);
  loadNotes();
});

function logEvent(message: string) {
  const log = document.getElementById('eventLog')!;
  const div = document.createElement('div');
  div.textContent = `${new Date().toLocaleTimeString()} ${message}`;
  log.prepend(div);
}

document.getElementById('noteText')!.addEventListener('keydown', e => {
  if (e.key === 'Enter') createNote();
});

document.getElementById('addBtn')!.addEventListener('click', createNote);

loadNotes();
