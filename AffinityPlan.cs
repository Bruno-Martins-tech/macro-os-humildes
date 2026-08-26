using System;

namespace MacroSupremes
{
    // Planejamento de afinidade de CPU adaptavel por maquina e por numero de instancias.
    // Logica pura (sem dependencias de SO/WinForms) para poder ser testada isoladamente.
    public static class AffinityPlan
    {
        // Mascara com todos os cores logicos ligados.
        public static long FullMask(int cores)
        {
            if (cores <= 0) return 1L;
            if (cores >= 63) return -1L; // todos os bits (evita overflow do shift em maquinas gigantes)
            return (1L << cores) - 1L;
        }

        // Mascara de afinidade para a instancia idx (0-based) de um total de instancias,
        // numa maquina com 'cores' cores logicos.
        //
        // Regras (adaptavel a cada maquina):
        //  - Maquina pequena (<=2 cores): NAO restringe (pinar so atrapalha).
        //  - 1 unica instancia: NAO restringe (usa a maquina inteira).
        //  - Mais instancias do que cores: NAO restringe (deixa o Windows equilibrar).
        //  - Caso contrario: cada instancia recebe um BLOCO proprio de cores, sem
        //    sobreposicao, espalhando a carga (ex.: 8 cores, 3 WYD -> 0-1, 2-3, 4-5).
        public static long MaskFor(int idx, int total, int cores)
        {
            if (cores <= 2 || total <= 1 || total > cores || idx < 0 || idx >= total)
                return FullMask(cores);

            int bloco = Math.Max(1, cores / total);
            long mask = 0;
            for (int k = 0; k < bloco; k++)
            {
                int core = idx * bloco + k;
                if (core < cores) mask |= (1L << core);
            }
            return mask == 0 ? FullMask(cores) : mask;
        }
    }
}
