// Dumps pointer/id tables around the official room chat action tables.
// Usage: analyzeHeadless <projectDir> <projectName> -process <program> -readOnly
//        -scriptPath tools/ghidra_scripts -postScript DumpRoomChatTables.java

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;

import java.nio.charset.Charset;

public class DumpRoomChatTables extends GhidraScript {
    private Memory memory;

    @Override
    protected void run() throws Exception {
        memory = currentProgram.getMemory();
        println("PROGRAM " + currentProgram.getName());

        if ("client.bin".equalsIgnoreCase(currentProgram.getName())) {
            dumpPointerTable("tw_male_keywords", "0088aae8", 0x35, "Big5");
            dumpIdTable("tw_male_ids", "0088abc0", 0x35);
            dumpPointerTable("tw_female_keywords", "0088ac98", 0x38, "Big5");
            dumpIdTable("tw_female_ids", "0088ad78", 0x38);
            dumpIdTable("tw_mot_ids", "0088ae58", 10);
            dumpPointerTable("tw_mot_labels", "0088ae94", 12 * 4, "Big5");
            return;
        }

        if ("sdo.bin".equalsIgnoreCase(currentProgram.getName())) {
            dumpPointerTable("cn_male_keywords", "00b8e6a0", 0x35, "GBK");
            dumpIdTable("cn_male_ids", "00b8e778", 0x35);
            dumpPointerTable("cn_female_keywords", "00b8e850", 0x38, "GBK");
            dumpIdTable("cn_female_ids", "00b8e930", 0x38);
            dumpIdTable("cn_mot_ids", "00b8ea10", 10);
            dumpPointerTable("cn_mot_labels", "00b8ea4c", 12 * 4, "GBK");
            return;
        }

        println("No room chat table definition for this program.");
    }

    private void dumpPointerTable(String label, String start, int count, String charsetName) throws Exception {
        Charset charset = Charset.forName(charsetName);
        Address base = toAddr(start);
        println("TABLE " + label + " ptr_start=0x" + start + " count=" + count + " charset=" + charsetName);
        for (int i = 0; i < count; i++) {
            Address entry = base.add((long) i * 4);
            long ptr = Integer.toUnsignedLong(memory.getInt(entry));
            String text = "";
            if (ptr != 0) {
                text = readCString(toAddr(Long.toHexString(ptr)), charset);
            }
            println(String.format("%s[%02d] entry=0x%s ptr=0x%08x text=%s",
                    label, i, entry, ptr, text));
        }
    }

    private void dumpIdTable(String label, String start, int count) throws Exception {
        Address base = toAddr(start);
        println("TABLE " + label + " id_start=0x" + start + " count=" + count);
        for (int i = 0; i < count; i++) {
            Address entry = base.add((long) i * 4);
            int value = memory.getInt(entry);
            println(String.format("%s[%02d] entry=0x%s value=%d", label, i, entry, value));
        }
    }

    private String readCString(Address address, Charset charset) throws Exception {
        byte[] buf = new byte[256];
        int n = 0;
        while (n < buf.length) {
            byte b = memory.getByte(address.add(n));
            if (b == 0) {
                break;
            }
            buf[n++] = b;
        }
        return new String(buf, 0, n, charset);
    }
}
